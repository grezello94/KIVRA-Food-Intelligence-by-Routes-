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
let apiOffline=false,physicalPrints=0,imports=0,cloudPrints=0;
const receipts=new Map(), importedKeys=new Set();
const json=(res,status,body)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(body))};
const read=req=>new Promise(resolve=>{let body='';req.on('data',x=>body+=x);req.on('end',()=>resolve(body?JSON.parse(body):{}))});
const appServer=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://localhost');
 if(url.pathname.startsWith('/api/')){
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
 res.setHeader('Access-Control-Allow-Origin',req.headers.origin||'*');res.setHeader('Access-Control-Allow-Methods','GET,POST,OPTIONS');res.setHeader('Access-Control-Allow-Headers','content-type,x-kivra-local-token');res.setHeader('Access-Control-Allow-Private-Network','true');
 if(req.method==='OPTIONS'){res.writeHead(204);return res.end()}
 if(req.url==='/v1/session')return json(res,200,{token:'local-secret',version:'1.7.0',queueName:'Test USB',deviceName:'Test PC'});
 if(req.url==='/v1/print'){
  const input=await read(req);if(receipts.has(input.idempotencyKey))return json(res,200,receipts.get(input.idempotencyKey));
  physicalPrints++;const operational=new Date(input.operationalDateTime),expiry=input.isReprint?new Date(input.expiryDateTime):new Date(operational.getTime()+86400000);
  const result={...input,status:'Printed',printJobId:input.labelId,idempotent:false,receipt:{request:input,operationalDateTime:operational.toISOString(),expiryDateTime:expiry.toISOString(),printedAt:new Date().toISOString(),payload:'TEST TSPL',status:'Printed'}};receipts.set(input.idempotencyKey,result);return json(res,200,result);
 }
 return json(res,404,{detail:'missing'});
});
test('offline reload, local USB printing, reprints and ordered reconnect in a real browser',{skip:!chromium,timeout:45000},async()=>{
 await new Promise(resolve=>appServer.listen(0,'127.0.0.1',resolve));
 await new Promise(resolve=>bridge.listen(17847,'127.0.0.1',resolve));
 const origin=`http://127.0.0.1:${appServer.address().port}`;
 const browser=await chromium.launch({headless:true,executablePath:process.env.KIVRA_CHROME_PATH||undefined,args:['--no-first-run']});
 const context=await browser.newContext();
 const errors=[];context.on('page',page=>page.on('pageerror',error=>errors.push(error.message)));
 let page=await context.newPage();
 try{
  await page.goto(origin);
  await page.locator('#pin').fill('1234');await page.locator('#loginForm button').click();
  await page.waitForFunction(()=>state.items.length===1&&snapshotLoaded);
  await page.evaluate(()=>navigator.serviceWorker.ready);
  await page.waitForFunction(()=>navigator.serviceWorker.controller!==null);
  await page.evaluate(()=>offlineClient.persist());
  await page.evaluate(()=>{state.view='sync';render()});
  await page.getByRole('button',{name:'Connect this PC'}).click();
  await page.waitForFunction(()=>Boolean(localStorage.kivraLocalPrinterId));
  assert.equal(await page.evaluate(()=>localStorage.kivraLocalPrinterId),ids.printer);
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
  if(process.env.KIVRA_BROWSER_SCREENSHOT)await page.screenshot({path:process.env.KIVRA_BROWSER_SCREENSHOT,fullPage:true});
  assert.deepEqual(errors,[]);
  console.log(JSON.stringify({passed:true,offlineReloadMs,physicalPrints,imports,cloudPrints,consoleErrors:errors},null,2));
 }finally{await browser.close();appServer.close();bridge.close()}
});
