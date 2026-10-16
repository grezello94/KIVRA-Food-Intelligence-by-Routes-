const CACHE='kivra-shell-v78';
// Precache the application only. Food photos are cached when they are viewed,
// so installation does not compete with startup/print requests for bandwidth.
const SHELL=['/index.html','/app.css?v=78','/printer.css?v=78','/visual-refresh.css?v=78','/command-center.css?v=78','/futuristic.css?v=78','/futuristic-panels.css?v=78','/assets/kivra-routes-logo.webp','/food-category-rules.js?v=78','/food-photos.js?v=78','/ui-icons.js?v=78','/app.js?v=78','/manifest.webmanifest'];
self.addEventListener('install',event=>event.waitUntil(caches.open(CACHE).then(cache=>cache.addAll(SHELL)).then(()=>self.skipWaiting())));
self.addEventListener('activate',event=>event.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(key=>key.startsWith('kivra-shell-')&&key!==CACHE).map(key=>caches.delete(key)))).then(()=>self.clients.claim())));
self.addEventListener('fetch',event=>{
 if(event.request.method!=='GET')return;
 const url=new URL(event.request.url);
 if(url.origin!==self.location.origin||url.pathname.startsWith('/api/'))return;
 const cacheResponse=response=>{
  if(response.ok){const copy=response.clone();event.waitUntil(caches.open(CACHE).then(cache=>cache.put(event.request,copy)))}
  return response;
 };
 if(event.request.mode==='navigate'){
  event.respondWith(fetch(event.request).then(cacheResponse).catch(async()=>await caches.match(event.request)||await caches.match('/index.html')||Response.error()));
  return;
 }
 // Versioned scripts and styles can be served immediately. A release changes
 // the URL version and cache name, so updated code is still fetched once.
 const versioned=url.searchParams.has('v')&&/\.(js|css)$/.test(url.pathname);
 const image=/\.(webp|png|jpe?g|svg|ico)$/.test(url.pathname);
 if(versioned||image){
  event.respondWith(caches.match(event.request).then(cached=>cached||fetch(event.request).then(cacheResponse)));
  return;
 }
 event.respondWith(fetch(event.request).then(cacheResponse).catch(async()=>await caches.match(event.request)||Response.error()));
});
