const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {resolve} = require('node:path');
const vm = require('node:vm');

const root = resolve(__dirname, '../..');
const source = readFileSync(resolve(root, 'src/Kivra.Api/wwwroot/app.js'), 'utf8');
const offlineSource = readFileSync(resolve(root, 'src/Kivra.Api/wwwroot/offline-client.js'), 'utf8');
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return {promise, resolve}; };

function application(options = {}) {
 const nodes = {
  '#app': {addEventListener() {}, innerHTML: ''},
  '#printerSelect': {value: 'printer-1', selectedOptions: [{text: 'USB kitchen'}]},
  '#storageSelect': {value: 'storage-1'},
  '#copies': {value: 1},
  '#batchPrinter': {value: 'printer-1'},
  '[data-action="print"]': {disabled: false},
  '#pin': {value: ''},
  '#loginDialog': {opened: false, showModal() { this.opened = true; }, close() { this.opened = false; }},
  '#offlineStatus': {setAttribute() {}},
  '#itemImagePreview': {style: {}}
 };
 const user = {id: 'chef-a', role: 'Administrator'};
 const caches = new Map([['chef-a', {items: [{id: 'item-1', name: 'Rice'}], categories: [], printers: [], locations: [], labels: [], expiry: []}]]);
 const sessionStorage = {kivraToken: options.token === undefined ? 'token-a' : options.token, kivraUser: JSON.stringify(user), ...(options.expiresAt ? {kivraExpiresAt: options.expiresAt} : {}), removeItem(key) { delete this[key]; }};
 const context = vm.createContext({
  sessionStorage,
  localStorage: {removeItem(key) { delete this[key]; }},
  navigator: {onLine: false},
  document: {querySelector: selector => nodes[selector], documentElement: {classList: {add() {}}}},
  MutationObserver: class {observe() {}},
  render() { context.renders++; }, renders: 0,
  showToast(message) { context.messages.push(message); }, messages: [],
  page: content => content, previewLabel: () => '',
  icon: () => '', setTimeout, clearTimeout, Uint8Array, AbortController,
  FormData: class {constructor(element) { this.fields = element.fields; } get(key) { return this.fields[key] ?? null; }},
  fetch: async () => { throw Error('Unexpected network request'); },
  KivraOffline: {
   async readSession() { context.sessionReads++; return options.savedSession || null; },
   async clearSession() {},
   async readBootstrap(userId) { return caches.get(userId) || null; },
   async saveBootstrap(userId, data) { caches.set(userId, JSON.parse(JSON.stringify(data))); },
   async listOperations() { return []; },
   async withSyncLock(userId, callback) { return callback(); }
  }, sessionReads: 0
 });
 vm.runInContext(offlineSource, context);
 vm.runInContext(source.slice(0, source.indexOf('function brandLogo')), context);
 function load(start, end) { vm.runInContext(source.slice(source.indexOf(start), source.indexOf(end, source.indexOf(start))), context); }
 load('async function printLabel()', 'function batchOrder()');
 load('async function executeBatchPrint(', 'function cachedReport(');
 load('function successView(', 'let expiryFilter');
 load('async function saveItem(', 'function showToast(');
 load('async function handleItemImage(', 'function resizeItemImage(');
 load('function signOut()', 'function normalizeItemStorageField(');
 load('async function start()', "matchMedia('(min-width:760px)')");
 return {context, nodes, run: code => vm.runInContext(code, context)};
}

test('an uncertain physical print stops a multiple-copy order for review', async () => {
 const {context, nodes, run} = application();
 nodes['#copies'].value = 3;
 context.printCalls = 0;
 run("state.selected={id:'item-1'};request=async path=>{if(path==='/api/labels/print'){printCalls++;return {status:'OfflinePending',requiresReview:true,failureReason:'Check whether the label printed.'}}return []}");
 await run('printLabel()');
 assert.equal(context.printCalls, 1, 'another label must not be sent after the preceding physical outcome is uncertain');
});

test('the pending print screen does not claim an uncertain physical print never printed', () => {
 const {run} = application();
 const html = run("successView({status:'OfflinePending',requiresReview:true,failureReason:'Check whether the label printed.'})");
 assert.doesNotMatch(html, /has not printed yet/i);
 assert.match(html, /check|uncertain|review/i);
});

test('an uncertain physical print stops the rest of a batch order', async () => {
 const {context, run} = application();
 context.printCalls = 0;
 context.submit = {preventDefault() {}, currentTarget: {querySelector: () => ({innerHTML: 'Print order'})}};
 run("batchOrder=()=>[{itemId:'item-1',storageLocationId:'storage-1',quantity:3}];request=async()=>{printCalls++;return {status:'OfflinePending',requiresReview:true,failureReason:'Check whether the label printed.'}}");
 await run('executeBatchPrint(submit)');
 assert.equal(context.printCalls, 1);
 assert.match(context.messages[0], /uncertain|check the physical label/i);
});

test('offline item saving tells the user it is saved locally and awaiting synchronization', async () => {
 const {context, run} = application();
 context.submit = {
  preventDefault() {},
  target: {
   fields: {name: 'Soup', categoryId: 'category-1', classification: 'Veg', dateTerminology: 'Prepared', shelfLifeValue: '2', shelfLifeUnit: 'Days'},
   querySelector: () => ({innerHTML: 'Save'})
  }
 };
 run("request=async()=>({id:'new-item',offlinePending:true});hydrate=async()=>{}");
 await run('saveItem(submit)');
 assert.doesNotMatch(context.messages[0], /saved to the database/i);
 assert.match(context.messages[0], /this PC|offline|sync/i);
});

test('an expired durable session cannot be bypassed by a token left in sessionStorage', async () => {
 const {context, nodes, run} = application({token: 'expired-token', expiresAt: new Date(Date.now() - 1).toISOString()});
 await run('start()');
 assert.ok(context.sessionReads > 0, 'startup must validate the durable session expiry even when a tab retained its token');
 assert.equal(run('state.token'), '');
 assert.equal(nodes['#loginDialog'].opened, true);
});

test('a valid durable session restores a closed page and cached kitchen data without the network', async () => {
 const savedSession = {token: 'restored-token', user: {id: 'chef-a', role: 'Administrator'}, expiresAt: new Date(Date.now() + 60000).toISOString()};
 const {context, nodes, run} = application({token: '', savedSession});
 await run('start()');
 assert.equal(run('state.token'), 'restored-token');
 assert.equal(run('state.items[0].name'), 'Rice');
 assert.equal(nodes['#loginDialog'].opened, false);
 assert.equal(context.sessionStorage.kivraExpiresAt, savedSession.expiresAt);
});

test('an item image finishing after an account switch cannot populate the new account draft', async () => {
 const {context, run} = application(), image = deferred();
 context.imageResult = image.promise;
 run('resizeItemImage=()=>imageResult;state.itemImageDraft=undefined');
 const pending = run("handleItemImage({target:{files:[{type:'image/jpeg'}]}})");
 run("signOut();state.token='token-b';state.user={id:'chef-b',role:'Administrator'};state.itemImageDraft=undefined");
 image.resolve('data:image/webp;base64,account-a-image');
 await pending;
 assert.equal(run('state.itemImageDraft'), undefined);
});
