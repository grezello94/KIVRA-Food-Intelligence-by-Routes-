const CACHE='kivra-shell-v83';
const SHELL=[
 '/index.html','/app.css?v=83','/printer.css?v=83','/visual-refresh.css?v=83',
 '/command-center.css?v=83','/futuristic.css?v=83','/futuristic-panels.css?v=83',
 '/offline.css?v=83','/assets/kivra-routes-logo.webp','/food-category-rules.js?v=83',
 '/food-photos.js?v=83','/ui-icons.js?v=83','/offline-store.js?v=83','/offline-client.js?v=83','/app.js?v=83',
 '/manifest.webmanifest'
];
// Cache the complete local photo catalog after the small application shell.
// Two concurrent downloads keep this background installation from occupying
// every connection needed by kitchen requests. A missing photo cannot prevent
// the shell from installing; it will be retried when that photo is displayed.
const PHOTOS=[
 '65-masala','ajinamoto','bbq','biryani-masala','boiled-chicken',
 'boiled-chicken-boneless','boiled-dal','boiled-egg','boiled-mutton',
 'boiled-mutton-boneless','boiled-noodles','boiled-prawns','boiled-rice',
 'bread-crumbs','butter','butter-chicken','chaat-masala','cheese',
 'chicken-crispy','chicken-lollipop','chicken-pakoda','chicken-pakora',
 'chilli-flakes','chilli-powder','chopped-chilli','chopped-garlic',
 'chopped-ginger','chopped-masala','chutney','coriander','coriander-powder',
 'curry-leaves','cut-capsicum','cut-carrot','cut-onion','fresh-cream',
 'fried-chicken','fried-kanda','fried-noodles','fried-onion','frozen-vegetables',
 'fry-chilly','garam-masala','geen-chutney','ginger-garlic-paste','gobi-pakoda',
 'green-chutney','haldi-powder','jeera-powder','kaju-magaj-till',
 'kanda-lasun-masala','kasuri-methi','kitchen-king-masala','maida','masala',
 'masala-powder','mayonnaise','mint','mix-veg','mushroom-pakoda','noodles',
 'onion-and-capsicum','paneer','prawns-pakoda','raita','raw-chicken',
 'raw-fish','raw-prawns','rawa','rechado-masala','schezwan-sauce',
 'spring-onion','sugar','tandoori-masala','tandoori-masala-powder','till',
 'veg-pakoda','white-pepper-masala','white-tandoori-masala','whole-spices'
].map(name=>`/assets/food/${name}.webp`).concat('/assets/kitchen-food-sprite-v2.webp');
const STATIC=new Set([...SHELL,...PHOTOS]);

async function cachePhotos(cache){
 let next=0;
 const controller=new AbortController();
 const timeout=setTimeout(()=>controller.abort(),10000);
 async function download(){
  while(next<PHOTOS.length&&!controller.signal.aborted){
   const path=PHOTOS[next++];
   try{
    const response=await fetch(new Request(new URL(path,self.location.origin),{cache:'reload',signal:controller.signal}));
    if(response.ok)await cache.put(path,response);
   }catch{/* Photos are optional; authenticated data never enters this cache. */}
  }
 }
 try{await Promise.all([download(),download()])}finally{clearTimeout(timeout)}
}

self.addEventListener('install',event=>event.waitUntil((async()=>{
 const cache=await caches.open(CACHE);
 // Bypass the HTTP cache so an update cannot install an old index alongside
 // the new scripts. A failed core download leaves the working worker in place.
 await cache.addAll(SHELL.map(path=>new Request(new URL(path,self.location.origin),{cache:'reload'})));
 await cachePhotos(cache);
 // Let updates wait until existing app windows close. Forcing activation here
 // would replace assets underneath a kitchen's open print form.
})()));

self.addEventListener('activate',event=>event.waitUntil((async()=>{
 const keys=await caches.keys();
 await Promise.all(keys.filter(key=>key.startsWith('kivra-shell-')&&key!==CACHE).map(key=>caches.delete(key)));
 await self.clients.claim();
})()));

async function cachedAsset(request,key,event){
 let cache;
 try{
  cache=await caches.open(CACHE);
  const cached=await cache.match(key);
  if(cached)return cached;
 }catch{/* Online use must still work when browser storage is unavailable. */}
 const response=await fetch(request);
 if(cache&&response.ok){
  const copy=response.clone();
  event.waitUntil(cache.put(key,copy).catch(()=>{}));
 }
 return response;
}

self.addEventListener('fetch',event=>{
 const request=event.request;
 if(request.method!=='GET')return;
 const url=new URL(request.url);
 // Only public, explicitly listed application files belong in this cache.
 // This excludes API/auth responses, reports, downloads and unrelated routes.
 if(url.origin!==self.location.origin||request.headers?.has('authorization'))return;
 if(request.mode==='navigate'){
  if(url.pathname!=='/'&&url.pathname!=='/index.html')return;
  // Navigation uses the same installed release as its scripts immediately,
  // even when a disconnected connection would take minutes to time out.
  event.respondWith(cachedAsset(request,'/index.html',event));
  return;
 }
 const key=url.pathname+url.search;
 if(STATIC.has(key))event.respondWith(cachedAsset(request,key,event));
});
