const {test}=require('node:test');
const assert=require('node:assert/strict');
const {readFileSync}=require('node:fs');
const {resolve}=require('node:path');
const vm=require('node:vm');
const root=resolve(__dirname,'../..');
const source=readFileSync(resolve(root,'src/Kivra.Api/wwwroot/app.js'),'utf8');

function application(){
 const nodes={
  '#app':{addEventListener(){}},
  '#printerSelect':{value:'printer',selectedOptions:[{text:'USB kitchen'}]},
  '#storageSelect':{value:'storage'},
  '#copies':{value:1},
  '[data-action="print"]':{disabled:false}
 };
 const context=vm.createContext({
  sessionStorage:{kivraToken:'session',removeItem(key){delete this[key]}},localStorage:{removeItem(key){delete this[key]}},
  document:{querySelector:selector=>nodes[selector],documentElement:{classList:{add(){}}}},
  MutationObserver:class{observe(){}},
  render(){context.renders++},renders:0,
  showToast:message=>context.messages.push(message),messages:[],
  icon:()=>'',setTimeout,Uint8Array,
  fetch:async()=>{throw Error('Unexpected network request')}
 });
 vm.runInContext(source.slice(0,source.indexOf('function brandLogo')),context);
 vm.runInContext(source.slice(source.indexOf('async function printLabel()'),source.indexOf('function batchOrder()')),context);
 const startOffset=source.indexOf('async function start()');
 vm.runInContext(source.slice(startOffset,source.indexOf("matchMedia('(min-width:760px)')",startOffset)),context);
 return {context,nodes,run:code=>vm.runInContext(code,context)};
}

test('cached bootstrap is usable before the server refresh returns',()=>{
 const {context,run}=application();
 context.sessionStorage.kivraBootstrap=JSON.stringify({items:[{id:'cached-item'}],printers:[{id:'printer',driver:'TscTsplUsb'}]});
 assert.equal(run('restoreBootstrap()'),true);
 assert.equal(run('state.items[0].id'),'cached-item');
 assert.equal(run('preferredPrinter().id'),'printer');
});

test('a background bootstrap refresh preserves an open print form',async()=>{
 const {context,run}=application();
 context.reply={items:[{id:'fresh-item'}]};
 run("state.view='print';request=async()=>reply");
 await run('hydrate()');
 assert.equal(context.renders,0);
 assert.equal(run('state.items[0].id'),'fresh-item');
 assert.equal(JSON.parse(context.sessionStorage.kivraBootstrap).items[0].id,'fresh-item');
});

test('an older bootstrap response cannot overwrite newly refreshed print history',async()=>{
 const {context,run}=application();
 let finishBootstrap;
 context.bootstrap=new Promise(resolve=>{finishBootstrap=resolve});
 context.history=[{id:'new-label',status:'Active',itemName:'Rice',expiryDateTime:'2026-10-18T00:00:00Z'}];
 run("request=async path=>path==='/api/bootstrap'?bootstrap:history");
 const pending=run('hydrate()');
 await run('refreshLabelActivity()');
 finishBootstrap({labels:[{id:'old-label'}],expiry:[]});
 await pending;
 assert.equal(run('state.labels[0].id'),'new-label');
 assert.equal(run('state.expiry[0].itemNameSnapshot'),'Rice');
});

test('printing refreshes label activity without downloading the full bootstrap again',async()=>{
 const {context,run}=application();
 context.paths=[];
 run("state.selected={id:'item'};request=async path=>{paths.push(path);return path==='/api/labels/print'?{status:'Queued',labelCode:'TEST'}:[]}");
 await run('printLabel()');
 assert.equal(run('state.view'),'success');
 assert.deepEqual(context.paths,['/api/labels/print','/api/labels?take=500']);
 assert.equal(run('state.busy'),false);
});

test('a failed copy stops a multiple-label order immediately',async()=>{
 const {context,nodes,run}=application();
 nodes['#copies'].value=3;
 context.paths=[];
 run("state.selected={id:'item'};request=async path=>{paths.push(path);return {status:'Failed',failureReason:'Offline'}}");
 await run('printLabel()');
 assert.deepEqual(context.paths,['/api/labels/print']);
 assert.deepEqual(context.messages,['Offline']);
 assert.equal(nodes['[data-action="print"]'].disabled,false);
});

test('a failed startup refresh keeps cached data and an open print form',async()=>{
 const {context,run}=application();
 context.sessionStorage.kivraBootstrap=JSON.stringify({items:[{id:'cached-item'}]});
 run("state.view='print';request=async()=>{throw Error('Network unavailable')}");
 await run('start()');
 assert.equal(run('state.view'),'print');
 assert.equal(run('state.dataError'),null);
 assert.equal(context.renders,1);
 assert.ok(context.messages[0].includes('Network unavailable'));
 assert.ok(context.sessionStorage.kivraBootstrap);
});

test('delayed print activity refresh updates history after the user navigates there',async()=>{
 const {context,run}=application();
 let finishHistory;
 context.history=new Promise(resolve=>{finishHistory=resolve});
 run("state.selected={id:'item'};request=async path=>path==='/api/labels/print'?{status:'Queued'}:history");
 await run('printLabel()');
 run("state.view='history'");
 const priorRenders=context.renders;
 finishHistory([{id:'new-label',status:'Active'}]);
 await new Promise(resolve=>setImmediate(resolve));
 assert.equal(run('state.labels[0].id'),'new-label');
 assert.equal(context.renders,priorRenders+1);
});

function serviceWorker(){
 const handlers={},stored=new Map(),network=[];
 const context=vm.createContext({
  URL,Response,
  self:{location:{origin:'https://kivra.test'},addEventListener:(name,handler)=>handlers[name]=handler,skipWaiting:async()=>{},clients:{claim:async()=>{}}},
  caches:{open:async()=>({addAll:async paths=>{context.precached=paths},put:async(key,value)=>stored.set(key.url||key,value)}),match:async key=>stored.get(key.url||key),keys:async()=>[]},
  fetch:async request=>{network.push(request.url);return new Response('network')}
 });
 vm.runInContext(readFileSync(resolve(root,'src/Kivra.Api/wwwroot/sw.js'),'utf8'),context);
 return {context,handlers,stored,network};
}

test('offline installation does not pre-download the food photo catalog or legacy sprite',async()=>{
 const {context,handlers}=serviceWorker();
 let done;
 handlers.install({waitUntil:promise=>done=promise});
 await done;
 assert.equal(context.precached.some(path=>path.includes('/assets/food/')||path.includes('sprite')),false);
 assert.ok(context.precached.includes('/app.js?v=78'));
});

test('cached versioned scripts are served immediately without a network request',async()=>{
 const {handlers,stored,network}=serviceWorker();
 const request={method:'GET',url:'https://kivra.test/app.js?v=78',mode:'cors'};
 stored.set(request.url,new Response('cached script'));
 let response;
 handlers.fetch({request,respondWith:promise=>response=promise,waitUntil(){}});
 assert.equal(await (await response).text(),'cached script');
 assert.equal(network.length,0);
});

test('API reads bypass the offline cache',()=>{
 const {handlers}=serviceWorker();
 handlers.fetch({request:{method:'GET',url:'https://kivra.test/api/bootstrap'},respondWith(){assert.fail('API requests must remain authenticated network requests')}});
});

test('offline navigation falls back to the cached application shell',async()=>{
 const {context,handlers,stored}=serviceWorker();
 context.fetch=async()=>{throw Error('offline')};
 stored.set('/index.html',new Response('offline shell'));
 let response;
 handlers.fetch({request:{method:'GET',url:'https://kivra.test/',mode:'navigate'},respondWith:promise=>response=promise,waitUntil(){}});
 assert.equal(await (await response).text(),'offline shell');
});
