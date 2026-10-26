const {test}=require('node:test');
const assert=require('node:assert/strict');
const {readFileSync,existsSync}=require('node:fs');
const {resolve}=require('node:path');
const vm=require('node:vm');
const webroot=resolve(__dirname,'../../src/Kivra.Api/wwwroot');
const origin='https://kivra.test';
const workerSource=readFileSync(resolve(webroot,'sw.js'),'utf8');

function serviceWorker(){
 const handlers={},stores=new Map(),network=[],deleted=[];
 const keyOf=request=>new URL(request.url||request,origin).href;
 const storeFor=name=>{if(!stores.has(name))stores.set(name,new Map());return stores.get(name)};
 const context=vm.createContext({
  URL,Request,Response,Set,Promise,AbortController,setTimeout,clearTimeout,
  self:{
   location:{origin},addEventListener:(name,handler)=>handlers[name]=handler,
   skipWaiting:async()=>{context.skipped=true},clients:{claim:async()=>{context.claimed=true}}
  },
  caches:{
   open:async name=>({
    addAll:async requests=>{
     context.precached=Array.from(requests);
     if(context.failCore)throw Error('Missing core asset');
     requests.forEach(request=>storeFor(name).set(keyOf(request),new Response('core')));
    },
    put:async(key,value)=>{if(context.failWrites)throw Error('Quota exceeded');storeFor(name).set(keyOf(key),value)},
    match:async key=>storeFor(name).get(keyOf(key))?.clone()
   }),
   keys:async()=>Array.from(stores.keys()),
   delete:async name=>{deleted.push(name);return stores.delete(name)}
  },
  fetch:async request=>{network.push(request);return new Response('network')}
 });
 vm.runInContext(workerSource,context);
 const current=vm.runInContext('CACHE',context);
 const cached=(key,response,cache=current)=>storeFor(cache).set(keyOf(key),new Response(response));
 function dispatchFetch(path,options={}){
  let response;
  const waits=[];
  handlers.fetch({
   request:{method:'GET',mode:'cors',headers:new Headers(),url:new URL(path,origin).href,...options},
   respondWith:promise=>{response=promise},waitUntil:promise=>waits.push(promise)
  });
  return {response,waits};
 }
 async function lifecycle(name){
  const waits=[];
  handlers[name]({waitUntil:promise=>waits.push(promise)});
  await Promise.all(waits);
 }
 return {context,network,stores,deleted,current,cached,dispatchFetch,lifecycle,run:code=>vm.runInContext(code,context)};
}

test('installation caches every script/style referenced by the page and all catalog photos',async()=>{
 const worker=serviceWorker();
 await worker.lifecycle('install');
 const shell=Array.from(worker.run('SHELL'));
 const html=readFileSync(resolve(webroot,'index.html'),'utf8');
 const references=[...html.matchAll(/(?:src|href)="(\/(?:[^"?]+\.(?:js|css))(?:\?[^\"]*)?)"/g)].map(match=>match[1]);
 assert.ok(references.length>0);
 for(const path of references)assert.ok(shell.includes(path),`Missing page asset ${path}`);
 for(const request of worker.context.precached){
  assert.equal(request.cache,'reload');
  assert.ok(existsSync(resolve(webroot,new URL(request.url).pathname.slice(1))),`Missing file ${request.url}`);
 }
 const photos=Array.from(worker.run('PHOTOS'));
 const catalog=readFileSync(resolve(webroot,'food-photos.js'),'utf8');
 for(const [path] of catalog.matchAll(/\/assets\/food\/[^"\s]+\.webp/g))assert.ok(photos.includes(path),`Photo omitted: ${path}`);
 for(const path of photos){
  assert.ok(existsSync(resolve(webroot,path.slice(1))),`Missing photo ${path}`);
  assert.ok(worker.stores.get(worker.current).has(new URL(path,origin).href),`Photo not cached: ${path}`);
 }
 assert.ok(photos.includes('/assets/kitchen-food-sprite-v2.webp'));
 assert.equal(worker.context.skipped,undefined,'An update must not replace an open print form');
});

test('photo downloads follow core caching, stay bounded, and cannot break installation',async()=>{
 const worker=serviceWorker();
 let active=0,maxActive=0;
 worker.context.fetch=async request=>{
  assert.ok(worker.context.precached,'Core shell must be cached before photos');
  maxActive=Math.max(maxActive,++active);
  await new Promise(resolve=>setImmediate(resolve));
  active--;
  if(request.url.includes('boiled-rice'))throw Error('Connection interrupted');
  return new Response('photo');
 };
 await worker.lifecycle('install');
 assert.equal(maxActive,2);
 assert.equal(worker.stores.get(worker.current).has(origin+'/assets/food/boiled-rice.webp'),false);
 assert.ok(worker.stores.get(worker.current).has(origin+'/index.html'));
});

test('a failed core download rejects installation without deleting the previous release',async()=>{
 const worker=serviceWorker();
 worker.cached('/index.html','working shell','kivra-shell-v79');
 worker.context.failCore=true;
 await assert.rejects(worker.lifecycle('install'),/Missing core asset/);
 assert.equal(worker.network.length,0);
 assert.ok(worker.stores.has('kivra-shell-v79'));
 assert.equal(worker.context.skipped,undefined);
});

test('a stalled photo connection cannot hold the shell installation open indefinitely',async()=>{
 const worker=serviceWorker();
 worker.context.setTimeout=callback=>setImmediate(callback);
 worker.context.clearTimeout=clearImmediate;
 worker.context.fetch=request=>new Promise((_,reject)=>request.signal.addEventListener('abort',()=>reject(Error('timeout')),{once:true}));
 await worker.lifecycle('install');
 assert.ok(worker.stores.get(worker.current).has(origin+'/index.html'));
});

test('cached scripts, photos, manifest and navigation never wait for the network',async()=>{
 const worker=serviceWorker();
 worker.context.fetch=()=>{throw Error('A cached resource must not request the network')};
 const script=worker.run("SHELL.find(path=>path.startsWith('/app.js?'))");
 for(const [path,key,options] of [
  [script,script,{}],
  ['/assets/food/raw-fish.webp','/assets/food/raw-fish.webp',{}],
  ['/manifest.webmanifest','/manifest.webmanifest',{}],
  ['/?standalone=1','/index.html',{mode:'navigate'}],
  ['/index.html','/index.html',{mode:'navigate'}]
 ]){
  worker.cached(key,'cached');
  const {response}=worker.dispatchFetch(path,options);
  assert.equal(await (await response).text(),'cached');
 }
});

test('public static misses are fetched and persisted for later offline use',async()=>{
 const worker=serviceWorker();
 const path='/assets/food/paneer.webp';
 const {response,waits}=worker.dispatchFetch(path);
 assert.equal(await (await response).text(),'network');
 await Promise.all(waits);
 worker.context.fetch=async()=>{throw Error('offline')};
 assert.equal(await (await worker.dispatchFetch(path).response).text(),'network');
});

test('authentication, API, reports, downloads and other origins bypass the shell cache',()=>{
 const worker=serviceWorker();
 for(const [path,options] of [
  ['/api/bootstrap',{}],['/api/auth/pin',{}],['/api/reports/labels.csv',{mode:'navigate'}],
  ['/healthz',{mode:'navigate'}],['/downloads/KIVRA-Windows-Print-Bridge.zip',{}],
  ['/private-photo.webp',{}],['/unlisted.js?v=80',{}],['/app.js?v=79',{}],
  ['https://images.example.test/photo.webp',{}],
  ['/index.html',{headers:new Headers({Authorization:'Bearer private'})}],
  ['/index.html',{method:'POST',mode:'navigate'}]
 ])assert.equal(worker.dispatchFetch(path,options).response,undefined,`Unexpected cache interception: ${path}`);
});

test('activation removes only obsolete KIVRA shell caches and claims clients',async()=>{
 const worker=serviceWorker();
 worker.cached('/index.html','new');
 worker.cached('/index.html','old','kivra-shell-v79');
 worker.cached('/data','private','other-app-cache');
 await worker.lifecycle('activate');
 assert.deepEqual(worker.deleted,['kivra-shell-v79']);
 assert.ok(worker.stores.has(worker.current));
 assert.ok(worker.stores.has('other-app-cache'));
 assert.equal(worker.context.claimed,true);
});

test('browser storage failures cannot prevent online use or turn HTTP errors into cached successes',async()=>{
 const worker=serviceWorker();
 worker.context.caches.open=async()=>{throw Error('Storage blocked')};
 assert.equal(await (await worker.dispatchFetch('/manifest.webmanifest').response).text(),'network');
 const second=serviceWorker();
 second.context.failWrites=true;
 const hit=second.dispatchFetch('/manifest.webmanifest');
 assert.equal(await (await hit.response).text(),'network');
 await Promise.all(hit.waits);
 const missing=serviceWorker();
 missing.context.fetch=async()=>new Response('not found',{status:404});
 const miss=missing.dispatchFetch('/manifest.webmanifest');
 assert.equal((await miss.response).status,404);
 assert.equal(miss.waits.length,0);
});

test('stylesheets and page contain no required remote assets',()=>{
 const files=['index.html','app.css','printer.css','visual-refresh.css','command-center.css','futuristic.css','futuristic-panels.css'];
 for(const file of files){
  const source=readFileSync(resolve(webroot,file),'utf8');
  assert.doesNotMatch(source,/(?:url\(['"]?|(?:src|href)=["'])https?:\/\//,`Remote asset in ${file}`);
 }
});
