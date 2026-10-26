const {test}=require('node:test');
let chromium;try{({chromium}=require(process.env.KIVRA_PLAYWRIGHT_MODULE||'playwright'))}catch{}
const http=require('node:http');
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../..');
const web=path.join(root,'src/Kivra.Api/wwwroot');
const ids={user:'10000000-0000-0000-0000-000000000001',category:'20000000-0000-0000-0000-000000000001',item:'30000000-0000-0000-0000-000000000001',printer:'40000000-0000-0000-0000-000000000001',storage:'50000000-0000-0000-0000-000000000001'};
const user={id:ids.user,displayName:'Offline Test',role:'Administrator'};
const category={id:ids.category,name:'Prepared Food',active:true};
const location={id:ids.storage,name:'Chiller 1',active:true};
const printer={id:ids.printer,name:'Kitchen USB',model:'TSC TE210',driver:'TscTsplUsb',usbQueueName:'Test USB',dpi:203,labelWidthMm:50,labelHeightMm:30,gapMm:2,mediaSensingMode:'Gap',printMethod:'DirectThermal',printSpeedIps:3,printDensity:8,enabled:true};
let data={items:[{id:ids.item,name:'Boiled Rice',categoryId:ids.category,category:'Prepared Food',classification:'Veg',dateTerminology:'PREPARED',shelfLifeValue:1,shelfLifeUnit:'Days',defaultStorageLocationId:ids.storage,storage:'Chiller 1',active:true}],categories:[category],printers:[printer],allPrinters:[printer],locations:[location],allLocations:[location],users:[user],labels:[],expiry:[],restaurantTimeZoneId:'Asia/Kolkata'};
let apiOffline=false,physicalPrints=0,imports=0,cloudPrints=0,cloudTests=0,pairPosts=0,localTests=0,testRequests=0,dropTestResponse=true;
let appOrigin='';
const receipts=new Map(),testReceipts=new Map(), importedKeys=new Set();
const json=(res,status,body)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(body))};
const read=req=>new Promise(resolve=>{let body='';req.on('data',x=>body+=x);req.on('end',()=>resolve(body?JSON.parse(body):{}))});
const appServer=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://localhost');
 if(url.pathname.startsWith('/api/')){
  // Expired bridge credentials must never be needed by same-PC USB printing.
  if(url.pathname==='/api/bridge/pairing-code'||url.pathname==='/api/bridge/pair'){pairPosts++;return json(res,401,{detail:'Saved cloud pairing rejected'});}
  if(url.pathname==='/api/bridge/jobs/claim')return json(res,401,{detail:'Saved cloud pairing rejected'});
  if(/^\/api\/printers\/[^/]+\/test-(connection|print)$/.test(url.pathname)){cloudTests++;return json(res,401,{detail:'Saved cloud pairing rejected'});}
  if(apiOffline)return json(res,503,{error:'Internet unavailable'});
  const input=await read(req);
  if(url.pathname==='/api/auth/pin')return json(res,200,{token:'browser-session',user,expiresAt:new Date(Date.now()+43200000).toISOString()});
  if(url.pathname==='/api/bootstrap')return json(res,200,data);
  if(url.pathname==='/api/labels'&&req.method==='GET')return json(res,200,data.labels);
  if(url.pathname==='/api/printers/all')return json(res,200,data.allPrinters);
  if(url.pathname==='/api/categories'&&req.method==='POST'){data.categories.push(input);return json(res,201,input)}
  if(url.pathname==='/api/items'&&req.method==='POST'){data.items.push({...input,category:data.categories.find(x=>x.id===input.categoryId)?.name,storage:'Chiller 1'});return json(res,201,input)}
  if(url.pathname==='/api/labels/offline-import'){
   if(!importedKeys.has(input.idempotencyKey)){
    importedKeys.add(input.idempotencyKey);imports++;
    const existing=data.labels.find(x=>x.id===input.labelId);
    if(input.isReprint){assert.ok(existing);existing.successfulPrintCount++}
    else data.labels.push({id:input.labelId,labelCode:input.labelCode,itemId:input.itemId,printerId:input.printerId,storageLocationId:input.storageLocationId,categorySnapshot:input.categorySnapshot,classificationSnapshot:input.classificationSnapshot,dateTerminologySnapshot:input.dateTerminologySnapshot,shelfLifeRuleSnapshot:`${input.shelfLifeValue} ${input.shelfLifeUnit}`,itemName:input.itemNameSnapshot,operationalDateTime:input.operationalDateTime,expiryDateTime:input.expiryDateTime,storageLocation:input.storageLocationSnapshot,status:'Active',lastPrintStatus:'Printed',successfulPrintCount:1,createdAt:input.printedAt});
   }
   return json(res,200,{labelId:input.labelId,labelCode:input.labelCode,status:'Printed',printJobId:input.labelId});
  }
  if(url.pathname==='/api/labels/print'){cloudPrints++;return json(res,200,{status:'Printed'})}
  return json(res,200,[]);
 }
 const file=path.join(web,url.pathname==='/'?'index.html':url.pathname);
 if(!file.startsWith(web)||!fs.existsSync(file)||fs.statSync(file).isDirectory()){res.writeHead(404);return res.end()}
 const ext=path.extname(file),types={'.html':'text/html','.js':'text/javascript','.css':'text/css','.webp':'image/webp','.webmanifest':'application/manifest+json'};
 res.writeHead(200,{'content-type':types[ext]||'application/octet-stream','cache-control':'no-cache'});fs.createReadStream(file).pipe(res);
});
const bridge=http.createServer(async(req,res)=>{
 if(req.headers.origin!==appOrigin)return json(res,403,{detail:'Untrusted website'});
 res.setHeader('Access-Control-Allow-Origin',appOrigin);res.setHeader('Access-Control-Allow-Methods','GET,POST,OPTIONS');res.setHeader('Access-Control-Allow-Headers','content-type,x-kivra-local-token');res.setHeader('Access-Control-Allow-Private-Network','true');
 if(req.method==='OPTIONS'){res.writeHead(204);return res.end()}
 const printerState={queueName:'Test USB',ready:true,needsSelection:false,printers:[{name:'Test USB',compatible:true,ready:true}],detail:'Windows queue is installed.'};
 if(req.url==='/v1/session')return json(res,200,{...printerState,token:'local-secret',version:'1.8.0',deviceName:'Test PC',capabilities:['connection-test','test-print','printer-selection','cloud-registration']});
 if(req.headers['x-kivra-local-token']!=='local-secret')return json(res,401,{detail:'Refresh the local session'});
 if(req.url==='/v1/printer'||req.url==='/v1/connection-test')return json(res,200,printerState);
 if(req.url==='/v1/test-print'){
  const input=await read(req);testRequests++;assert.ok(input.idempotencyKey);assert.equal(input.printer.driver,'TscTsplUsb');assert.equal(input.labelId,undefined);
  if(testReceipts.has(input.idempotencyKey))return json(res,200,{...testReceipts.get(input.idempotencyKey),idempotent:true});
  localTests++;const result={idempotencyKey:input.idempotencyKey,printJobId:input.idempotencyKey,status:'Printed',failureReason:null,idempotent:false};testReceipts.set(input.idempotencyKey,result);
  if(dropTestResponse){dropTestResponse=false;return res.destroy()}
  return json(res,200,result);
 }
 if(req.url.startsWith('/v1/test-prints/'))return testReceipts.has(decodeURIComponent(req.url.slice(16)))?json(res,200,testReceipts.get(decodeURIComponent(req.url.slice(16)))):json(res,404,{detail:'missing'});
 if(req.url.startsWith('/v1/jobs/'))return receipts.has(decodeURIComponent(req.url.slice(9)))?json(res,200,receipts.get(decodeURIComponent(req.url.slice(9)))):json(res,404,{detail:'missing'});
 if(req.url==='/v1/print'){
  const input=await read(req);if(receipts.has(input.idempotencyKey))return json(res,200,receipts.get(input.idempotencyKey));
  physicalPrints++;const operational=new Date(input.operationalDateTime),expiry=input.isReprint?new Date(input.expiryDateTime):new Date(operational.getTime()+86400000);
  const result={...input,status:'Printed',printJobId:input.labelId,idempotent:false,receipt:{request:input,operationalDateTime:operational.toISOString(),expiryDateTime:expiry.toISOString(),printedAt:new Date().toISOString(),payload:'TEST TSPL',status:'Printed'}};receipts.set(input.idempotencyKey,result);return json(res,200,result);
 }
 return json(res,404,{detail:'missing'});
});
test('automatic Windows USB discovery ignores rejected cloud pairing and supports offline reload, test recovery and reprints',{skip:!chromium,timeout:45000},async()=>{
 await new Promise(resolve=>appServer.listen(0,'127.0.0.1',resolve));
 appOrigin=`http://127.0.0.1:${appServer.address().port}`;
 await new Promise(resolve=>bridge.listen(0,'127.0.0.1',resolve));
 const mockBridgeOrigin=`http://127.0.0.1:${bridge.address().port}`;
 const origin=appOrigin;
 const browser=await chromium.launch({headless:true,executablePath:process.env.KIVRA_CHROME_PATH||undefined,args:['--no-first-run']});
 const context=await browser.newContext();
 // Every browser request to the product's fixed loopback address is proxied
 // to this test-only random port. Never contact or stop an installed bridge.
 await context.route('http://127.0.0.1:17847/**',async route=>{
  try{const response=await route.fetch({url:route.request().url().replace('http://127.0.0.1:17847',mockBridgeOrigin)});await route.fulfill({response});}
  catch{await route.abort('failed');}
 });
 await context.addInitScript(()=>{Object.defineProperty(navigator,'platform',{get:()=> 'Win32'});Object.defineProperty(navigator,'userAgentData',{get:()=>undefined});});
 const errors=[];context.on('page',page=>page.on('pageerror',error=>errors.push(error.message)));
 let page=await context.newPage();
 try{
  await page.goto(origin);
  await page.locator('#pin').fill('1234');await page.locator('#loginForm button').click();
  await page.waitForFunction(()=>state.items.length===1&&snapshotLoaded);
  await page.evaluate(()=>navigator.serviceWorker.ready);
  await page.waitForFunction(()=>navigator.serviceWorker.controller!==null);
  await page.evaluate(()=>offlineClient.persist());
  // The installed bridge and Windows queue are found at login without a
  // Connect-this-PC button, pairing code, or cloud-authenticated printer test.
  await page.waitForFunction(()=>localPrinterState?.ready&&Boolean(localStorage.kivraLocalPrinterId));
  assert.equal(await page.evaluate(()=>localStorage.kivraLocalPrinterId),ids.printer);
  await page.evaluate(()=>{state.view='more';render()});
  assert.equal(await page.locator('.printer-panel .panel-status').innerText(),'Ready');
  await page.evaluate(()=>testPrint()); // Accepted by the bridge; its reply is deliberately lost.
  assert.equal(localTests,1);assert.equal(testRequests,1);assert.equal(cloudTests,0);assert.equal(pairPosts,0);
  assert.equal(await page.evaluate(()=>state.labels.length),0);
  await page.reload();await page.waitForFunction(()=>snapshotLoaded&&localPrinterState?.ready);
  await page.evaluate(()=>testPrint()); // A fresh page recovers the persisted key without another label.
  assert.equal(localTests,1);assert.equal(testRequests,2);assert.equal(cloudTests,0);assert.equal(pairPosts,0);
  assert.equal((await page.evaluate(()=>offlineClient.operations())).length,0);
  apiOffline=true;
  await context.setOffline(true);
  await page.close();page=await context.newPage();const start=Date.now();await page.goto(origin,{waitUntil:'domcontentloaded'});
  await page.waitForFunction(()=>state.items.length===1&&state.user?.id==='10000000-0000-0000-0000-000000000001');
  const offlineReloadMs=Date.now()-start;assert.ok(offlineReloadMs<3000,`offline startup ${offlineReloadMs}ms`);
  assert.equal(await page.locator('#loginDialog').evaluate(x=>x.open),false);
  await context.setOffline(false); // Internet API stays unavailable; loopback USB stays reachable.
  await page.evaluate(()=>{offlineClient.emit({online:false});state.selected=state.items[0];state.view='print';render()});
  await page.getByRole('button',{name:'Print label',exact:true}).click();
  await page.getByRole('heading',{name:'Label printed',exact:true}).waitFor();
  assert.equal(physicalPrints,1);assert.equal(cloudPrints,0);assert.equal(imports,0);
  let operations=await page.evaluate(()=>offlineClient.operations());
  assert.equal(operations.length,1);assert.equal(operations[0].path,'/api/labels/offline-import');
  assert.equal(await page.evaluate(()=>state.labels[0].lastPrintStatus),'Printed');
  await page.reload();await page.waitForFunction(()=>snapshotLoaded&&state.labels.length===1);
  assert.equal(await page.evaluate(()=>state.labels[0].lastPrintStatus),'Printed');assert.equal(physicalPrints,1);
  const originalDates=await page.evaluate(()=>({prepared:state.labels[0].operationalDateTime,expiry:state.labels[0].expiryDateTime}));
  await page.evaluate(async()=>{state.selectedLabel=state.labels[0];await reprintLabel()});
  assert.equal(physicalPrints,2);
  await page.reload();await page.waitForFunction(()=>snapshotLoaded&&state.labels.length===1);
  assert.equal(await page.evaluate(()=>state.labels[0].successfulPrintCount),2);
  assert.deepEqual(await page.evaluate(()=>({prepared:state.labels[0].operationalDateTime,expiry:state.labels[0].expiryDateTime})),originalDates);
  apiOffline=false;await page.waitForFunction(()=>!offlineClient.status.syncing);await page.evaluate(()=>offlineClient.sync());await page.waitForFunction(()=>!offlineClient.status.syncing&&offlineClient.status.pending===0);
  assert.equal(imports,2);assert.equal(physicalPrints,2);assert.equal(cloudPrints,0);
  assert.equal((await page.evaluate(()=>offlineClient.operations())).length,0);
  // A new category and item created offline retain stable IDs and replay in dependency order.
  apiOffline=true;await page.evaluate(()=>offlineClient.emit({online:false}));
  const categoryId=await page.evaluate(async()=>{const c=await request('/api/categories',{method:'POST',body:JSON.stringify({name:'Offline Category',active:true})});return c.id});
  await page.evaluate(async categoryId=>{await request('/api/items',{method:'POST',body:JSON.stringify({name:'Offline Soup',categoryId,classification:'Veg',dateTerminology:'PREPARED',shelfLifeValue:1,shelfLifeUnit:'Days',labelType:'Food',active:true,defaultStorageLocationId:state.locations[0].id})})},categoryId);
  await page.reload();await page.waitForFunction(()=>state.items.some(x=>x.name==='Offline Soup'));
  apiOffline=false;await page.waitForFunction(()=>!offlineClient.status.syncing);await page.evaluate(()=>offlineClient.sync());await page.waitForFunction(()=>!offlineClient.status.syncing&&offlineClient.status.pending===0);
  assert.ok(data.items.some(x=>x.name==='Offline Soup'&&x.categoryId===categoryId));
  assert.equal((await page.evaluate(()=>offlineClient.operations())).length,0);
  // A partially completed form survives reconnect without a full DOM replacement.
  await page.evaluate(()=>{state.selected=state.items[0];state.view='print';render()});
  await page.locator('#copies').evaluate(x=>x.value='3');await page.evaluate(()=>offlineClient.sync());
  assert.equal(await page.locator('#copies').evaluate(x=>x.value),'3');
  if(process.env.KIVRA_BROWSER_SCREENSHOT){await page.evaluate(()=>{state.view='more';render()});await page.screenshot({path:process.env.KIVRA_BROWSER_SCREENSHOT,fullPage:true});}
  assert.deepEqual(errors,[]);
  assert.equal(cloudTests,0);assert.equal(pairPosts,0);
  console.log(JSON.stringify({passed:true,offlineReloadMs,physicalPrints,localTests,testRequests,imports,cloudPrints,cloudTests,pairPosts,consoleErrors:errors},null,2));
 }finally{await browser.close();appServer.close();bridge.close()}
});
