const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {resolve} = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../src/Kivra.Api/wwwroot/offline-client.js'), 'utf8');
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
const disconnected = () => Object.assign(Error('Connection lost'), {network: true});
const deferred = () => { let resolve, reject; const promise = new Promise((done, fail) => { resolve = done; reject = fail; }); return {promise, resolve, reject}; };

function memoryStore() {
 const queues = new Map(), caches = new Map(), events = [];
 const store = {
  events, beforePut: null,
  async listOperations(userId) { return clone([...(queues.get(userId)?.values() || [])]); },
  async putOperation(userId, operation) {
   events.push({type: 'saving', userId, operation: clone(operation)});
   await store.beforePut?.(userId, operation);
   if (!queues.has(userId)) queues.set(userId, new Map());
   queues.get(userId).set(operation.id, clone(operation));
   events.push({type: 'committed', userId, operation: clone(operation)});
  },
  async removeOperation(userId, id) { queues.get(userId)?.delete(id); events.push({type: 'removed', userId, id}); },
  async saveBootstrap(userId, data) { caches.set(userId, clone(data)); events.push({type: 'cached', userId}); },
  async readBootstrap(userId) { return clone(caches.get(userId) || null); },
  async withSyncLock(userId, callback) { return callback(); }
 };
 return store;
}

function snapshot() {
 const printers = [{id: 'printer-1', name: 'USB kitchen', enabled: true, driver: 'TscTsplUsb'}];
 const locations = [{id: 'storage-1', name: 'Chiller', active: true}];
 return {
  restaurantTimeZoneId: 'Asia/Kolkata',
  items: [{id: 'item-1', name: 'Rice', categoryId: 'category-1', category: 'Prepared', classification: 'Vegetarian', dateTerminology: 'UseBy', shelfLifeValue: 2, shelfLifeUnit: 'Days'}],
  categories: [{id: 'category-1', name: 'Prepared', active: true}],
  printers, allPrinters: printers, locations, allLocations: locations,
  labels: [], expiry: [], users: [], bridgeDevices: []
 };
}

function application(options = {}) {
 const store = options.store || memoryStore(), events = store.events;
 const context = vm.createContext({setTimeout, clearTimeout, AbortController, fetch: async () => { throw Error('Unexpected bridge request'); }});
 vm.runInContext(source, context);
 let session = {token: 'token-a', user: {id: 'chef-a', role: 'Administrator'}}, data = options.snapshot || snapshot(), hasSnapshot = options.hasSnapshot !== false, counter = 0;
 let network = options.network || (async (path, request) => clone(request.body ? JSON.parse(request.body) : {}));
 const fixture = {
  store, events, networkCalls: [], statuses: [], synced: 0,
  get data() { return data; },
  get session() { return session; },
  setNetwork(callback) { network = callback; },
  switchAccount(userId = 'chef-b', replacement = snapshot()) { session = {token: `token-${userId}`, user: {id: userId, role: 'Administrator'}}; data = replacement; },
  setRole(role) { session.user.role = role; }
 };
 fixture.client = new context.KivraOfflineClient({
  store,
  getSession: () => session,
  getSnapshot: () => data,
  setSnapshot: value => { data = clone(value); events.push({type: 'snapshot', userId: session.user.id}); },
  hasSnapshot: () => hasSnapshot,
  network: async (path, request = {}) => {
   const call = {path, options: clone(request), userId: session.user.id};
   fixture.networkCalls.push(call);
   events.push({type: 'network', ...call});
   return network(path, request);
  },
  newId: () => `generated-${++counter}`,
  localPrinterId: () => options.localPrinter ? 'printer-1' : null,
  previewExpiry: () => '2026-10-20T10:00:00.000Z',
  onStatus: status => fixture.statuses.push(clone(status)),
  onSynced: async () => { fixture.synced++; }
 });
 return fixture;
}

function write(body = {}) { return {method: 'POST', body: JSON.stringify(body)}; }
function printBody() { return {itemId: 'item-1', printerId: 'printer-1', storageLocationId: 'storage-1', operationalDateTime: '2026-10-18T10:00:00Z'}; }
function receipt() { return {status: 'Printed', receipt: {operationalDateTime: '2026-10-18T10:00:00Z', expiryDateTime: '2026-10-20T10:00:00Z', printedAt: '2026-10-18T10:01:00Z', payload: 'TSPL payload'}}; }
function historicalLabel() {
 return {
  id: 'original-label', labelCode: 'KIVRA-ORIGINAL', itemId: 'item-1', printerId: 'printer-1', storageLocationId: 'storage-1',
  itemName: 'Original rice', storageLocation: 'Original chiller', categorySnapshot: 'Original category', classificationSnapshot: 'NonVeg', dateTerminologySnapshot: 'Opened', shelfLifeRuleSnapshot: '4 Hours',
  operationalDateTime: '2026-10-01T01:30:00Z', expiryDateTime: '2026-10-01T05:30:00Z', createdAt: '2026-10-01T01:30:00Z', status: 'Used', lastPrintStatus: 'Printed', successfulPrintCount: 3
 };
}

test('a mutation commits to durable storage before making a server request', async () => {
 const fixture = application(), gate = deferred(), entered = deferred();
 fixture.store.beforePut = async () => { entered.resolve(); await gate.promise; };
 const pending = fixture.client.request('/api/items', write({name: 'Soup'}));
 await entered.promise;
 assert.equal(fixture.networkCalls.length, 0);
 gate.resolve();
 const result = await pending;
 assert.equal(result.name, 'Soup');
 assert.ok(fixture.events.findIndex(event => event.type === 'committed') < fixture.events.findIndex(event => event.type === 'network'));
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
});

test('a failed durable queue write never sends or claims that the operation was queued', async () => {
 const fixture = application();
 fixture.store.beforePut = () => { throw Error('Storage full'); };
 await assert.rejects(fixture.client.request('/api/items', write({name: 'Soup'})), /Storage full/);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal(fixture.data.items.length, 1);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
});

test('a lost print response reuses its durable idempotency key after a page reload', async () => {
 const first = application({network: async () => { throw disconnected(); }});
 const pending = await first.client.request('/api/labels/print', write(printBody()));
 assert.equal(pending.offlinePending, true);
 const queued = (await first.store.listOperations('chef-a'))[0];
 assert.equal(first.networkCalls[0].options.headers['Idempotency-Key'], queued.id);
 assert.equal(JSON.parse(first.networkCalls[0].options.body).idempotencyKey, queued.id, 'the print API also requires its replay key in the JSON body');
 const reloaded = application({store: first.store, snapshot: await first.store.readBootstrap('chef-a'), network: async () => ({status: 'Queued', labelId: 'server-label'})});
 await reloaded.client.initialize();
 await reloaded.client.sync();
 assert.equal(reloaded.networkCalls[0].options.headers['Idempotency-Key'], queued.id);
 assert.equal(reloaded.networkCalls[0].options.body, first.networkCalls[0].options.body);
 assert.equal((await first.store.listOperations('chef-a')).length, 0);
 assert.equal(reloaded.synced, 1);
});

test('locally printed receipts retry only history import, with no second physical print', async () => {
 const first = application({localPrinter: true});
 let physicalPrints = 0;
 first.client.localFetch = async path => {
  if (path === '/v1/session') return {token: 'bridge-token'};
  assert.equal(path, '/v1/print');
  physicalPrints++;
  return receipt();
 };
 const printed = await first.client.request('/api/labels/print', write(printBody()));
 assert.equal(printed.status, 'Printed');
 assert.equal(printed.locallyPrinted, true);
 assert.equal(first.networkCalls.length, 0);
 assert.equal(physicalPrints, 1);
 let queued = (await first.store.listOperations('chef-a'))[0];
 assert.equal(queued.path, '/api/labels/offline-import');
 assert.equal(queued.type, 'server');
 assert.equal(JSON.parse(queued.body).payload, 'TSPL payload');
 first.setNetwork(async () => { throw disconnected(); });
 await first.client.sync();
 queued = (await first.store.listOperations('chef-a'))[0];
 const reloaded = application({store: first.store, snapshot: await first.store.readBootstrap('chef-a'), localPrinter: true, network: async () => ({status: 'Printed', labelId: JSON.parse(queued.body).labelId})});
 reloaded.client.localFetch = async () => { physicalPrints++; throw Error('The bridge must not be called for an existing receipt'); };
 await reloaded.client.initialize();
 await reloaded.client.sync();
 assert.equal(physicalPrints, 1);
 assert.equal((await first.store.listOperations('chef-a')).length, 0);
 assert.deepEqual([...first.networkCalls, ...reloaded.networkCalls].map(call => call.path), ['/api/labels/offline-import', '/api/labels/offline-import']);
 assert.equal(first.networkCalls[0].options.headers['Idempotency-Key'], reloaded.networkCalls[0].options.headers['Idempotency-Key']);
});

test('initialization and replay only read the currently signed-in account', async () => {
 const fixture = application();
 await fixture.store.saveBootstrap('chef-a', {items: [{id: 'account-a'}]});
 await fixture.store.saveBootstrap('chef-b', {items: [{id: 'account-b'}]});
 fixture.client.emit({online: false});
 await fixture.client.request('/api/categories', write({name: 'A category'}));
 fixture.switchAccount();
 assert.deepEqual(await fixture.client.initialize(), {items: [{id: 'account-b'}]});
 assert.equal(fixture.client.status.pending, 0);
 await fixture.client.sync();
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 1);
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
});

test('server validation and forbidden responses remove the attempted queue entry', async () => {
 for (const status of [400, 403, 422]) {
  const fixture = application({network: async () => { throw Object.assign(Error('Rejected'), {status}); }});
  await assert.rejects(fixture.client.request('/api/items', write({name: ''})), /Rejected/);
  assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
  assert.equal(fixture.client.status.pending, 0);
  assert.equal(fixture.data.items.length, 1);
 }
});

test('offline category and item creation preserve generated IDs and dependency order', async () => {
 const fixture = application();
 fixture.client.emit({online: false});
 const category = await fixture.client.request('/api/categories', write({name: 'Desserts', active: true}));
 const item = await fixture.client.request('/api/items', write({name: 'Custard', categoryId: category.id, active: true}));
 assert.equal(category.offlinePending, true);
 assert.equal(item.offlinePending, true);
 assert.equal(fixture.data.items.find(value => value.id === item.id).category, 'Desserts');
 const saved = await fixture.store.listOperations('chef-a');
 await fixture.client.sync();
 assert.deepEqual(fixture.networkCalls.map(call => call.path), ['/api/categories', '/api/items']);
 assert.equal(JSON.parse(fixture.networkCalls[0].options.body).id, category.id);
 assert.equal(JSON.parse(fixture.networkCalls[1].options.body).id, item.id);
 assert.equal(JSON.parse(fixture.networkCalls[1].options.body).categoryId, category.id);
 assert.deepEqual(fixture.networkCalls.map(call => call.options.headers['Idempotency-Key']), saved.map(operation => operation.id));
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
});

test('cached kitchen reads survive a network outage and unknown reads still fail', async () => {
 const fixture = application({network: async () => { throw disconnected(); }});
 assert.deepEqual(clone(await fixture.client.request('/api/items')), fixture.data.items);
 assert.equal(fixture.client.status.online, false);
 assert.deepEqual(clone(await fixture.client.request('/api/bootstrap')), fixture.data);
 assert.equal(fixture.networkCalls.length, 1);
 await assert.rejects(fixture.client.request('/api/system/storage'), /Connection lost/);
 const empty = application({hasSnapshot: false, network: async () => { throw disconnected(); }});
 await assert.rejects(empty.client.request('/api/items'), /Connection lost/);
});

test('pending local changes stay visible without a stale server read overwriting them', async () => {
 const fixture = application();
 fixture.client.emit({online: false});
 await fixture.client.request('/api/items', write({name: 'Custard', categoryId: 'category-1'}));
 fixture.client.emit({online: true});
 const bootstrap = await fixture.client.request('/api/bootstrap');
 assert.ok(bootstrap.items.some(item => item.name === 'Custard'));
 assert.equal(fixture.networkCalls.length, 0);
});

test('a blocked dependency stops later requests until an explicit successful retry', async () => {
 const fixture = application();
 fixture.client.emit({online: false});
 const category = await fixture.client.request('/api/categories', write({name: 'Desserts', active: true}));
 await fixture.client.request('/api/items', write({name: 'Custard', categoryId: category.id}));
 fixture.setNetwork(async () => { throw Object.assign(Error('Resolve duplicate category'), {status: 409}); });
 await fixture.client.sync();
 assert.deepEqual(fixture.networkCalls.map(call => call.path), ['/api/categories']);
 assert.equal(fixture.client.status.blocked, 1);
 assert.equal(fixture.client.status.pending, 2);
 await fixture.client.sync();
 assert.equal(fixture.networkCalls.length, 1);
 fixture.setNetwork(async (path, request) => JSON.parse(request.body));
 await fixture.client.sync({retryBlocked: true});
 assert.deepEqual(fixture.networkCalls.map(call => call.path), ['/api/categories', '/api/categories', '/api/items']);
 assert.equal(fixture.client.status.pending, 0);
});

test('an uncertain local physical print stays blocked through automatic and manual sync retries', async () => {
 const fixture = application({localPrinter: true});
 let physicalPrints = 0;
 fixture.client.localFetch = async path => {
  if (path === '/v1/session') return {token: 'bridge-token'};
  physicalPrints++;
  return {status: 'Uncertain', failureReason: 'Check whether the label printed.'};
 };
 const result = await fixture.client.request('/api/labels/print', write(printBody()));
 assert.equal(result.status, 'OfflinePending');
 assert.equal(result.requiresReview, true);
 assert.equal(result.printUncertain, true);
 assert.match(result.failureReason, /Check whether/);
 assert.equal((await fixture.store.listOperations('chef-a'))[0].status, 'blocked');
 await fixture.client.sync();
 await fixture.client.sync({retryBlocked: true});
 assert.equal(physicalPrints, 1);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal(fixture.client.status.pending, 1);
});

test('a lost local print response asks for printer review before creating additional copies', async () => {
 const fixture = application({localPrinter: true});
 fixture.client.localFetch = async path => {
  if (path === '/v1/session') return {token: 'bridge-token'};
  throw disconnected();
 };
 const result = await fixture.client.request('/api/labels/print', write(printBody()));
 assert.equal(result.status, 'OfflinePending');
 assert.equal(result.requiresReview, true);
 assert.equal(result.printUncertain, true);
 assert.equal((await fixture.store.listOperations('chef-a'))[0].type, 'local-print');
});

test('authentication, user administration, and discovery actions cannot enter the offline queue', async () => {
 const fixture = application();
 fixture.client.emit({online: false});
 for (const path of ['/api/auth/pin', '/api/users', '/api/bridge/pairing-code', '/api/printers/printer-1/test-print']) {
  await assert.rejects(fixture.client.request(path, write({pin: '1234'})), /needs a connection/);
 }
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
 assert.equal(fixture.networkCalls.length, 0);
 fixture.setRole('KitchenStaff');
 await assert.rejects(fixture.client.request('/api/items', write({name: 'Not allowed'})), /Administrator access/);
 await assert.rejects(fixture.client.request('/api/labels/label-1/status', {method: 'PATCH', body: JSON.stringify({status: 'Used'})}), /Supervisor or Administrator/);
});

test('switching accounts during durable queue commit cannot send old work as the new user', async () => {
 const fixture = application(), gate = deferred(), entered = deferred();
 fixture.store.beforePut = async () => { entered.resolve(); await gate.promise; };
 const pending = fixture.client.request('/api/items', write({name: 'Account A soup'})).catch(error => error);
 await entered.promise;
 fixture.switchAccount('chef-b', {...snapshot(), items: [{id: 'b-only', name: 'Account B rice'}]});
 gate.resolve();
 await pending;
 assert.equal(fixture.networkCalls.length, 0, 'an operation captured for A must not use B credentials');
 assert.deepEqual(fixture.data.items.map(item => item.id), ['b-only']);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 1);
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
});

test('switching accounts while a local print is in flight retains its receipt under the original user', async () => {
 const fixture = application({localPrinter: true}), gate = deferred(), entered = deferred();
 fixture.client.localFetch = async path => {
  if (path === '/v1/session') return {token: 'bridge-token'};
  entered.resolve();
  return gate.promise;
 };
 const pending = fixture.client.request('/api/labels/print', write(printBody())).catch(error => error);
 await entered.promise;
 fixture.switchAccount('chef-b', {...snapshot(), labels: [{id: 'b-only'}]});
 gate.resolve(receipt());
 await pending;
 const original = await fixture.store.listOperations('chef-a');
 assert.equal(original.length, 1);
 assert.equal(original[0].path, '/api/labels/offline-import');
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
 assert.deepEqual(fixture.data.labels.map(label => label.id), ['b-only']);
 assert.equal(await fixture.store.readBootstrap('chef-b'), null);
});

test('a queued mutation cannot become new-account work while an earlier request finishes', async () => {
 const fixture = application(), gate = deferred(), entered = deferred();
 fixture.setNetwork(async () => { entered.resolve(); return gate.promise; });
 const first = fixture.client.request('/api/items', write({name: 'First A soup'})).catch(error => error);
 await entered.promise;
 const second = fixture.client.request('/api/items', write({name: 'Second A soup'})).catch(error => error);
 fixture.switchAccount('chef-b', {...snapshot(), items: [{id: 'b-only'}]});
 gate.resolve({id: 'first-a'});
 await Promise.all([first, second]);
 assert.equal(fixture.networkCalls.length, 1, 'an action submitted by A must not start with B credentials');
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
 assert.deepEqual(fixture.data.items.map(item => item.id), ['b-only']);
});

test('sync stops before server import when the account changes during a local print', async () => {
 const fixture = application({localPrinter: true}), gate = deferred(), entered = deferred();
 const localRequest = fixture.client.printSnapshot(printBody(), 'saved-print');
 await fixture.store.putOperation('chef-a', {id: 'saved-print', path: '/api/labels/print', method: 'POST', body: JSON.stringify(printBody()), createdAt: '2026-10-18T10:00:00Z', status: 'pending', type: 'local-print', localRequest});
 fixture.client.localFetch = async path => {
  if (path === '/v1/session') return {token: 'bridge-token'};
  entered.resolve();
  return gate.promise;
 };
 const syncing = fixture.client.sync();
 await entered.promise;
 fixture.switchAccount('chef-b', {...snapshot(), labels: [{id: 'b-only'}]});
 gate.resolve(receipt());
 await syncing;
 assert.equal(fixture.networkCalls.length, 0, 'account A receipt must not import using B credentials');
 assert.equal((await fixture.store.listOperations('chef-a'))[0].path, '/api/labels/offline-import');
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
 assert.deepEqual(fixture.data.labels.map(label => label.id), ['b-only']);
});

test('an in-flight server response stays bound to its original token and does not update the new account', async () => {
 const fixture = application(), gate = deferred(), entered = deferred();
 fixture.setNetwork(async () => { entered.resolve(); return gate.promise; });
 const pending = fixture.client.request('/api/items', write({name: 'Account A soup'})).catch(error => error);
 await entered.promise;
 assert.equal(fixture.networkCalls[0].options.headers.Authorization, 'Bearer token-a');
 fixture.switchAccount('chef-b', {...snapshot(), items: [{id: 'b-only'}]});
 const statusCount = fixture.statuses.length;
 gate.resolve({id: 'account-a-item'});
 const outcome = await pending;
 assert.equal(outcome.code, 'ACCOUNT_CHANGED');
 assert.deepEqual(fixture.data.items.map(item => item.id), ['b-only']);
 assert.equal(fixture.statuses.length, statusCount);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0, 'a confirmed A operation can be removed from the A queue');
 assert.equal(await fixture.store.readBootstrap('chef-b'), null);
});

test('an authentication failure retains saved work for the original account to sign in again', async () => {
 const fixture = application({network: async () => { throw Object.assign(Error('Session expired'), {status: 401}); }});
 await assert.rejects(fixture.client.request('/api/items', write({name: 'Soup'})), /Session expired/);
 const queued = await fixture.store.listOperations('chef-a');
 assert.equal(queued.length, 1);
 assert.equal(queued[0].status, 'pending');
 assert.match(queued[0].error, /Sign in again/);
 fixture.switchAccount();
 await fixture.client.initialize();
 await fixture.client.sync();
 assert.equal(fixture.networkCalls.length, 1);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 1);
});

test('a local reprint preserves original label dates and rules after catalog settings have changed', async () => {
 const data = snapshot(), original = historicalLabel();
 data.labels = [clone(original)];
 data.items[0] = {...data.items[0], name: 'Renamed rice', category: 'New category', classification: 'Veg', dateTerminology: 'Prepared', shelfLifeValue: 9, shelfLifeUnit: 'Months'};
 data.locations[0].name = 'New chiller';
 const fixture = application({snapshot: data, localPrinter: true});
 let physicalRequest, physicalPrints = 0;
 fixture.client.localFetch = async (path, options) => {
  if (path === '/v1/session') return {token: 'bridge-token'};
  physicalPrints++;
  physicalRequest = JSON.parse(options.body);
  return {...receipt(), labelId: original.id, receipt: {...receipt().receipt, operationalDateTime: original.operationalDateTime, expiryDateTime: original.expiryDateTime}};
 };
 const result = await fixture.client.request(`/api/labels/${original.id}/reprint`, write({idempotencyKey: 'reprint-key'}));
 assert.equal(result.status, 'Printed');
 assert.equal(result.locallyPrinted, true);
 assert.equal(physicalPrints, 1);
 assert.equal(physicalRequest.idempotencyKey, 'reprint-key');
 assert.equal(physicalRequest.isReprint, true);
 assert.equal(physicalRequest.labelId, original.id);
 assert.equal(physicalRequest.labelCode, original.labelCode);
 assert.equal(physicalRequest.itemNameSnapshot, original.itemName);
 assert.equal(physicalRequest.storageLocationSnapshot, original.storageLocation);
 assert.equal(physicalRequest.categorySnapshot, original.categorySnapshot);
 assert.equal(physicalRequest.classificationSnapshot, original.classificationSnapshot);
 assert.equal(physicalRequest.dateTerminologySnapshot, original.dateTerminologySnapshot);
 assert.equal(physicalRequest.shelfLifeValue, 4);
 assert.equal(physicalRequest.shelfLifeUnit, 'Hours');
 assert.equal(physicalRequest.operationalDateTime, original.operationalDateTime);
 assert.equal(physicalRequest.expiryDateTime, original.expiryDateTime);
 assert.equal(fixture.data.labels.length, 1);
 assert.equal(fixture.data.labels[0].id, original.id);
 assert.equal(fixture.data.labels[0].status, 'Used');
 assert.equal(fixture.data.labels[0].expiryDateTime, original.expiryDateTime);
 assert.equal(fixture.data.labels[0].successfulPrintCount, 4);
 const queued = (await fixture.store.listOperations('chef-a'))[0], imported = JSON.parse(queued.body);
 assert.equal(queued.path, '/api/labels/offline-import');
 assert.equal(imported.isReprint, true);
 assert.equal(imported.labelId, original.id);
 assert.equal(imported.operationalDateTime, original.operationalDateTime);
 assert.equal(imported.expiryDateTime, original.expiryDateTime);
 fixture.client.applyOptimistic(queued, {status: 'Printed'});
 fixture.client.applyOptimistic(queued, {status: 'Printed'});
 assert.equal(fixture.data.labels[0].successfulPrintCount, 4, 'restoring the same receipt must not increment the local reprint count again');
 await fixture.client.persist();
 const reloaded = application({store: fixture.store, snapshot: await fixture.store.readBootstrap('chef-a'), localPrinter: true, network: async () => { throw disconnected(); }});
 reloaded.client.localFetch = async () => { physicalPrints++; throw Error('Receipt replay must not physically reprint'); };
 reloaded.client.applyOptimistic(queued, {status: 'Printed'});
 assert.equal(reloaded.data.labels[0].successfulPrintCount, 4);
 await reloaded.client.sync();
 reloaded.setNetwork(async () => ({status: 'Printed', labelId: original.id}));
 await reloaded.client.sync();
 assert.equal(physicalPrints, 1);
 assert.equal(reloaded.data.labels[0].successfulPrintCount, 4);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
 assert.deepEqual(reloaded.networkCalls.map(call => call.path), ['/api/labels/offline-import', '/api/labels/offline-import']);
 assert.ok(reloaded.networkCalls.every(call => call.options.headers['Idempotency-Key'] === 'reprint-key'));
 assert.ok(reloaded.networkCalls.every(call => JSON.parse(call.options.body).isReprint));
});

test('a local reprint requires original history fields and never substitutes current item rules', async () => {
 const data = snapshot();
 data.labels = [{...historicalLabel(), shelfLifeRuleSnapshot: undefined}];
 const fixture = application({snapshot: data, localPrinter: true});
 fixture.client.localFetch = async () => assert.fail('incomplete historic details cannot be sent to a physical printer');
 await assert.rejects(fixture.client.request('/api/labels/original-label/reprint', write({})), /Reconnect once/);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal(fixture.data.labels[0].successfulPrintCount, 3);
});

test('a category cannot be discarded while saved item work depends on its generated ID', async () => {
 const fixture = application();
 fixture.client.emit({online: false});
 const category = await fixture.client.request('/api/categories', write({name: 'Desserts', active: true}));
 await fixture.client.request('/api/items', write({name: 'Custard', categoryId: category.id}));
 fixture.setNetwork(async () => { throw Object.assign(Error('Duplicate category'), {status: 409}); });
 await fixture.client.sync();
 const parent = (await fixture.store.listOperations('chef-a'))[0];
 assert.equal(parent.status, 'blocked');
 await assert.rejects(fixture.client.discard(parent.id), /depends on this request/);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 2);
 assert.ok(fixture.data.categories.some(value => value.id === category.id));
 const child = (await fixture.store.listOperations('chef-a'))[1];
 assert.equal(child.status, 'pending');
 await fixture.client.discard(child.id);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 1);
 assert.equal(fixture.data.items.some(value => value.name === 'Custard'), false);
 await fixture.client.discard(parent.id);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
 assert.equal(fixture.data.categories.some(value => value.id === category.id), false);
 assert.equal(fixture.client.status.pending, 0);
 const cached = await fixture.store.readBootstrap('chef-a');
 assert.equal(cached.items.some(value => value.name === 'Custard'), false);
 assert.equal(cached.categories.some(value => value.id === category.id), false);
});

test('discarding a blocked new entity rolls it back from cached active and management lists', async () => {
 const cases = [
  {path: '/api/items', body: {name: 'New soup', categoryId: 'category-1'}, fields: ['items']},
  {path: '/api/categories', body: {name: 'New category', active: true}, fields: ['categories']},
  {path: '/api/storage-locations', body: {name: 'New freezer', active: true}, fields: ['locations', 'allLocations']},
  {path: '/api/printers', body: {name: 'New printer', enabled: true}, fields: ['printers', 'allPrinters']}
 ];
 for (const scenario of cases) {
  const fixture = application();
  fixture.client.emit({online: false});
  const saved = await fixture.client.request(scenario.path, write(scenario.body));
  assert.ok(scenario.fields.every(field => fixture.data[field].some(value => value.id === saved.id)));
  fixture.setNetwork(async () => { throw Object.assign(Error('Validation rejected'), {status: 422}); });
  await fixture.client.sync();
  const queued = (await fixture.store.listOperations('chef-a'))[0];
  await fixture.client.discard(queued.id);
  assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
  const cached = await fixture.store.readBootstrap('chef-a');
  for (const field of scenario.fields) {
   assert.equal(fixture.data[field].some(value => value.id === saved.id), false, `${field} must no longer include the discarded entity`);
   assert.equal(cached[field].some(value => value.id === saved.id), false);
  }
  assert.equal(fixture.client.status.pending, 0);
 }
});

test('discarding an uncertain new label removes its optimistic history and expiry entries', async () => {
 const fixture = application({localPrinter: true});
 fixture.client.localFetch = async path => path === '/v1/session' ? {token: 'bridge-token'} : {status: 'Uncertain'};
 const result = await fixture.client.request('/api/labels/print', write(printBody()));
 assert.equal(result.requiresReview, true);
 assert.equal(fixture.data.labels.length, 1);
 assert.equal(fixture.data.expiry.length, 1);
 const queued = (await fixture.store.listOperations('chef-a'))[0];
 await fixture.client.discard(queued.id);
 assert.equal(fixture.data.labels.length, 0);
 assert.equal(fixture.data.expiry.length, 0);
 assert.equal((await fixture.store.readBootstrap('chef-a')).labels.length, 0);
});

test('a printed receipt cannot be discarded even after the history import is blocked', async () => {
 const fixture = application({localPrinter: true});
 fixture.client.localFetch = async path => path === '/v1/session' ? {token: 'bridge-token'} : receipt();
 await fixture.client.request('/api/labels/print', write(printBody()));
 fixture.setNetwork(async () => { throw Object.assign(Error('Resolve history conflict'), {status: 409}); });
 await fixture.client.sync();
 const queued = (await fixture.store.listOperations('chef-a'))[0];
 assert.equal(queued.path, '/api/labels/offline-import');
 assert.equal(queued.status, 'blocked');
 await assert.rejects(fixture.client.discard(queued.id), /printed label must be synced/);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 1);
 assert.equal(fixture.data.labels[0].successfulPrintCount, 1);
});

test('discarding a pending item edit restores its last saved values', async () => {
 const fixture = application(), original = clone(fixture.data.items[0]);
 fixture.client.emit({online: false});
 await fixture.client.request('/api/items/item-1', {method: 'PUT', body: JSON.stringify({...original, name: 'Changed rice', shelfLifeValue: 12})});
 assert.equal(fixture.data.items[0].name, 'Changed rice');
 const queued = (await fixture.store.listOperations('chef-a'))[0];
 assert.equal(queued.status, 'pending');
 await fixture.client.discard(queued.id);
 assert.deepEqual(fixture.data.items[0], original);
 assert.deepEqual((await fixture.store.readBootstrap('chef-a')).items[0], original);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
});

test('discarding an uncertain reprint keeps the historical label and clears its pending flags', async () => {
 const data = snapshot(), original = historicalLabel();
 data.labels = [clone(original)];
 const fixture = application({snapshot: data, localPrinter: true});
 fixture.client.localFetch = async path => path === '/v1/session' ? {token: 'bridge-token'} : {status: 'Uncertain'};
 await fixture.client.request('/api/labels/original-label/reprint', write({}));
 assert.equal(fixture.data.labels[0].offlinePending, true);
 const queued = (await fixture.store.listOperations('chef-a'))[0];
 await fixture.client.discard(queued.id);
 assert.equal(fixture.data.labels.length, 1);
 assert.equal(fixture.data.labels[0].id, original.id);
 assert.equal(fixture.data.labels[0].status, original.status);
 assert.equal(fixture.data.labels[0].successfulPrintCount, original.successfulPrintCount);
 assert.equal(fixture.data.labels[0].operationalDateTime, original.operationalDateTime);
 assert.equal(fixture.data.labels[0].expiryDateTime, original.expiryDateTime);
 assert.equal(fixture.data.labels[0].offlinePending, false);
 assert.equal(fixture.data.labels[0].lastPrintStatus, 'Printed');
});

test('discarding a label status change restores both history and the expiry monitor', async () => {
 const data = snapshot(), original = {...historicalLabel(), status: 'Active'};
 data.labels = [clone(original)];
 data.expiry = [{id: original.id, currentStatus: 'Active', expiryDateTime: original.expiryDateTime}];
 const fixture = application({snapshot: data});
 fixture.client.emit({online: false});
 await fixture.client.request('/api/labels/original-label/status', {method: 'PATCH', body: JSON.stringify({status: 'Used'})});
 assert.equal(fixture.data.labels[0].status, 'Used');
 assert.equal(fixture.data.expiry.length, 0);
 const queued = (await fixture.store.listOperations('chef-a'))[0];
 await fixture.client.discard(queued.id);
 assert.equal(fixture.data.labels[0].status, 'Active');
 assert.equal(fixture.data.expiry.length, 1);
 assert.equal(fixture.data.expiry[0].id, original.id);
 assert.equal(fixture.data.expiry[0].currentStatus, 'Active');
 assert.equal((await fixture.store.readBootstrap('chef-a')).expiry[0].currentStatus, 'Active');
});
