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
 const store = options.store || memoryStore(), events = store.events, localTests = options.localTests || new Map();
 const context = vm.createContext({setTimeout, clearTimeout, AbortController, fetch: async () => { throw Error('Unexpected bridge request'); }});
 vm.runInContext(source, context);
 let session = {token: 'token-a', user: {id: 'chef-a', role: 'Administrator'}}, data = options.snapshot || snapshot(), hasSnapshot = options.hasSnapshot !== false, counter = 0;
 let localPrinterId = typeof options.localPrinter === 'string' ? options.localPrinter : options.localPrinter ? 'printer-1' : null;
 let network = options.network || (async (path, request) => clone(request.body ? JSON.parse(request.body) : {}));
 const fixture = {
  store, events, localTests, networkCalls: [], statuses: [], synced: 0,
  get data() { return data; },
  get session() { return session; },
  get localPrinterId() { return localPrinterId; },
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
  autoLocal: options.autoLocal === true,
  requireLocal: options.requireLocal === true,
  localPrinterId: () => localPrinterId,
  onLocalPrinter: (printerId, bridgeSession) => { localPrinterId = printerId; options.onLocalPrinter?.(printerId, bridgeSession); },
  loadLocalTest: printerId => clone(localTests.get(`${session.user.id}:${printerId}`)),
  saveLocalTest: (printerId, body) => {
   const key = `${session.user.id}:${printerId}`;
   if (body) localTests.set(key, clone(body)); else localTests.delete(key);
  },
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

function detectedUsbSession(overrides = {}) {
 return {
  token: 'local-bridge-token', queueName: 'TSC TA220', ready: true,
  printers: [{name: 'TSC TA220', compatible: true, ready: true}],
  capabilities: ['connection-test', 'test-print', 'printer-selection', 'cloud-registration'],
  ...overrides
 };
}

test('an automatically detected USB printer prints without cloud pairing or valid cloud bridge credentials', async () => {
 const data = snapshot();
 data.printers[0].usbQueueName = 'tsc ta220';
 const fixture = application({snapshot: data, autoLocal: true, network: async () => { throw Object.assign(Error('Unpaired cloud bridge'), {status: 401}); }});
 const localCalls = [];
 fixture.client.localFetch = async (path, options = {}) => {
  localCalls.push({path, options});
  if (path === '/v1/session') return detectedUsbSession();
  assert.equal(path, '/v1/print');
  return receipt();
 };

 const result = await fixture.client.request('/api/labels/print', write(printBody()));

 assert.equal(result.locallyPrinted, true);
 assert.equal(result.status, 'Printed');
 assert.equal(fixture.localPrinterId, 'printer-1');
 assert.deepEqual(localCalls.map(call => call.path), ['/v1/session', '/v1/print']);
 assert.equal(fixture.networkCalls.length, 0);
 const saved = await fixture.store.listOperations('chef-a');
 assert.equal(saved.length, 1);
 assert.equal(saved[0].path, '/api/labels/offline-import');
 assert.equal(JSON.parse(saved[0].body).printerId, 'printer-1');
});

test('automatic local discovery also preserves the original snapshot for a reprint', async () => {
 const data = snapshot(), original = historicalLabel();
 data.labels = [clone(original)];
 data.items[0].shelfLifeValue = 999;
 const fixture = application({snapshot: data, autoLocal: true});
 let localRequest;
 fixture.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  localRequest = JSON.parse(options.body);
  return receipt();
 };

 const result = await fixture.client.request('/api/labels/original-label/reprint', write({}));

 assert.equal(result.locallyPrinted, true);
 assert.equal(localRequest.isReprint, true);
 assert.equal(localRequest.labelId, original.id);
 assert.equal(localRequest.labelCode, original.labelCode);
 assert.equal(localRequest.shelfLifeValue, 4);
 assert.equal(localRequest.shelfLifeUnit, 'Hours');
 assert.equal(localRequest.operationalDateTime, original.operationalDateTime);
 assert.equal(localRequest.expiryDateTime, original.expiryDateTime);
 assert.equal(fixture.networkCalls.length, 0);
});

test('a discovery failure before local dispatch may use the configured cloud print route', async () => {
 const fixture = application({autoLocal: true, network: async () => ({status: 'Queued', labelId: 'server-label'})});
 const localCalls = [];
 fixture.client.localFetch = async path => { localCalls.push(path); throw disconnected(); };

 const result = await fixture.client.request('/api/labels/print', write(printBody()));

 assert.equal(result.status, 'Queued');
 assert.deepEqual(localCalls, ['/v1/session']);
 assert.equal(fixture.networkCalls.length, 1);
 assert.equal(fixture.networkCalls[0].path, '/api/labels/print');
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
});

test('an already bound USB printer stays on its local route when discovery is unavailable', async () => {
 const fixture = application({autoLocal: true, localPrinter: true});
 fixture.client.localFetch = async () => { throw disconnected(); };

 const result = await fixture.client.request('/api/labels/print', write(printBody()));

 assert.equal(result.status, 'OfflinePending');
 assert.equal(result.requiresReview, true);
 assert.equal(fixture.networkCalls.length, 0);
 const saved = await fixture.store.listOperations('chef-a');
 assert.equal(saved.length, 1);
 assert.equal(saved[0].type, 'local-print');
});

test('an ambiguous automatic local print never falls back to cloud printing and reuses its saved request', async () => {
 const fixture = application({autoLocal: true, network: async () => { throw Object.assign(Error('Cloud authentication expired'), {status: 401}); }});
 const requests = [];
 fixture.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  assert.equal(path, '/v1/print');
  requests.push(options.body);
  if (requests.length === 1) throw disconnected();
  return receipt();
 };

 const result = await fixture.client.request('/api/labels/print', write(printBody()));
 assert.equal(result.requiresReview, true);
 assert.equal(fixture.networkCalls.length, 0);
 const original = (await fixture.store.listOperations('chef-a'))[0];
 assert.equal(original.type, 'local-print');

 await fixture.client.sync();
 assert.equal(requests.length, 2);
 assert.equal(requests[1], requests[0], 'retry must use the identical durable key and label snapshot');
 assert.equal(JSON.parse(requests[1]).idempotencyKey, original.id);
 assert.deepEqual(fixture.networkCalls.map(call => call.path), ['/api/labels/offline-import']);
 assert.equal((await fixture.store.listOperations('chef-a'))[0].path, '/api/labels/offline-import');

 await fixture.client.sync();
 assert.equal(requests.length, 2, 'a physical receipt must retry history import only');
 assert.ok(fixture.networkCalls.every(call => call.path === '/api/labels/offline-import'));
});

for (const driver of ['EpsonEscPosUsb', 'TscTsplNetwork', 'AndroidBridge', 'Fake']) {
 test(`automatic discovery does not route a ${driver} configuration through TSC USB`, async () => {
  const data = snapshot();
  data.printers[0].driver = driver;
  const fixture = application({snapshot: data, autoLocal: true, localPrinter: true, network: async () => ({status: 'Queued'})});
  fixture.client.localFetch = async () => assert.fail('unsupported printer configurations must not reach the TSPL bridge');

  const result = await fixture.client.request('/api/labels/print', write(printBody()));

  assert.equal(result.status, 'Queued');
  assert.equal(fixture.networkCalls.length, 1);
  assert.equal(fixture.networkCalls[0].path, '/api/labels/print');
 });
}

test('automatic discovery respects a different configured USB queue', async () => {
 const data = snapshot();
 data.printers[0].usbQueueName = 'Other kitchen TSC';
 const fixture = application({snapshot: data, autoLocal: true, network: async () => ({status: 'Queued'})});
 const paths = [];
 fixture.client.localFetch = async path => { paths.push(path); return detectedUsbSession(); };

 const result = await fixture.client.request('/api/labels/print', write(printBody()));

 assert.equal(result.status, 'Queued');
 assert.deepEqual(paths, ['/v1/session']);
 assert.equal(fixture.localPrinterId, null);
 assert.equal(fixture.networkCalls[0].path, '/api/labels/print');
});

test('automatic discovery binds a current printer ID without rewriting saved work from an older database', async () => {
 const fixture = application({autoLocal: true, localPrinter: 'deleted-printer'});
 const oldRequest = {...fixture.client.printSnapshot(printBody(), 'old-key'), printerId: 'deleted-printer'};
 const oldOperation = {id: 'old-key', path: '/api/labels/print', method: 'POST', body: JSON.stringify({...printBody(), printerId: 'deleted-printer', idempotencyKey: 'old-key'}), createdAt: '2026-10-18T09:00:00Z', status: 'blocked', type: 'local-print', localRequest: oldRequest};
 await fixture.store.putOperation('chef-a', oldOperation);
 fixture.client.localFetch = async path => path === '/v1/session' ? detectedUsbSession() : receipt();

 const result = await fixture.client.request('/api/labels/print', write(printBody()));

 assert.equal(result.locallyPrinted, true);
 assert.equal(fixture.localPrinterId, 'printer-1');
 const operations = await fixture.store.listOperations('chef-a');
 assert.deepEqual(operations.find(operation => operation.id === 'old-key'), clone(oldOperation));
 const current = operations.find(operation => operation.id !== 'old-key');
 assert.equal(current.path, '/api/labels/offline-import');
 assert.equal(JSON.parse(current.body).printerId, 'printer-1');
});

test('switching accounts during automatic discovery cannot bind or dispatch the earlier account print', {timeout: 5000}, async () => {
 const fixture = application({autoLocal: true}), entered = deferred(), gate = deferred();
 fixture.client.localFetch = async path => {
  assert.equal(path, '/v1/session');
  entered.resolve();
  return gate.promise;
 };

 const pending = fixture.client.request('/api/labels/print', write(printBody())).catch(error => error);
 await entered.promise;
 fixture.switchAccount('chef-b', {...snapshot(), labels: [{id: 'b-only'}]});
 gate.resolve(detectedUsbSession());
 const error = await pending;

 assert.match(error.message, /account changed/);
 assert.equal(fixture.localPrinterId, null);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
 assert.deepEqual(fixture.data.labels.map(label => label.id), ['b-only']);
});

test('a timed out local test retains its test key until resolved without creating kitchen history or cloud requests', async () => {
 const fixture = application({autoLocal: true});
 const requests = [];
 fixture.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  assert.equal(path, '/v1/test-print');
  const body = JSON.parse(options.body);
  requests.push(body);
  if (requests.length === 1) throw disconnected();
  return {idempotencyKey: body.idempotencyKey, printJobId: 'test-job', status: 'Printed', idempotent: requests.length === 2};
 };

 await assert.rejects(fixture.client.testLocal(fixture.data.printers[0]), /interrupted.*physical label/);
 await fixture.client.testLocal(fixture.data.printers[0]);
 assert.equal(requests[1].idempotencyKey, requests[0].idempotencyKey);
 assert.deepEqual(requests[1], requests[0]);
 await fixture.client.testLocal(fixture.data.printers[0]);
 assert.notEqual(requests[2].idempotencyKey, requests[1].idempotencyKey);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
 assert.equal(fixture.data.labels.length, 0);
});

test('a rejected local test token refreshes once and keeps the same test key', async () => {
 const fixture = application({autoLocal: true});
 const requests = [];
 let sessionCalls = 0;
 fixture.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') { sessionCalls++; return detectedUsbSession({token: sessionCalls === 1 ? 'expired-local-token' : 'local-bridge-token'}); }
  assert.equal(path, '/v1/test-print');
  const body = JSON.parse(options.body);
  requests.push({body, token: options.headers['X-Kivra-Local-Token']});
  if (requests.length === 1) throw Object.assign(Error('Local token expired'), {status: 401});
  return {idempotencyKey: body.idempotencyKey, printJobId: 'test-job', status: 'Printed'};
 };

 await fixture.client.testLocal(fixture.data.printers[0]);

 assert.equal(sessionCalls, 2, 'one initial connection plus one refresh after a definite 401 rejection');
 assert.equal(requests.length, 2);
 assert.equal(requests[0].token, 'expired-local-token');
 assert.equal(requests[1].token, 'local-bridge-token');
 assert.deepEqual(requests[0].body, requests[1].body);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal((await fixture.store.listOperations('chef-a')).length, 0);
});

test('required local mode saves a new USB label locally when initial discovery is unavailable', async () => {
 const fixture = application({autoLocal: true, requireLocal: true});
 const paths = [];
 fixture.client.localFetch = async path => { paths.push(path); throw disconnected(); };

 const result = await fixture.client.request('/api/labels/print', write(printBody()));

 assert.equal(result.status, 'OfflinePending');
 assert.equal(result.requiresReview, true);
 assert.equal(fixture.networkCalls.length, 0);
 assert.ok(paths.every(path => path === '/v1/session'), 'no print endpoint can be dispatched before connecting');
 const saved = (await fixture.store.listOperations('chef-a'))[0];
 assert.equal(saved.type, 'local-print');
 assert.equal(saved.localRequest.printerId, 'printer-1');
 assert.equal(saved.localRequest.idempotencyKey, saved.id);
});

test('a pending local test survives a reload with its original key and stock settings', async () => {
 const first = application({autoLocal: true});
 let original;
 first.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  original = JSON.parse(options.body);
  throw disconnected();
 };
 await assert.rejects(first.client.testLocal(first.data.printers[0]), /interrupted/);
 assert.deepEqual(first.localTests.get('chef-a:printer-1'), original);

 const updated = snapshot();
 updated.printers[0].labelWidthMm = 80;
 const reloaded = application({snapshot: updated, autoLocal: true, localTests: first.localTests});
 let retry;
 reloaded.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  retry = JSON.parse(options.body);
  return {idempotencyKey: retry.idempotencyKey, status: 'Printed', idempotent: true};
 };

 await reloaded.client.testLocal(reloaded.data.printers[0]);

 assert.deepEqual(retry, original, 'changing current stock settings must not turn receipt recovery into another physical test');
 assert.equal(reloaded.localTests.has('chef-a:printer-1'), false);
 assert.equal(reloaded.networkCalls.length, 0);
 assert.equal(reloaded.data.labels.length, 0);
});

test('pending local test recovery remains isolated from another signed-in account', async () => {
 const fixture = application({autoLocal: true});
 const requests = [];
 fixture.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  const body = JSON.parse(options.body);
  requests.push(body);
  if (requests.length === 1) throw disconnected();
  return {idempotencyKey: body.idempotencyKey, status: 'Printed'};
 };
 await assert.rejects(fixture.client.testLocal(fixture.data.printers[0]), /interrupted/);

 fixture.switchAccount();
 await fixture.client.testLocal(fixture.data.printers[0]);

 assert.notEqual(requests[1].idempotencyKey, requests[0].idempotencyKey);
 assert.deepEqual(fixture.localTests.get('chef-a:printer-1'), requests[0]);
 assert.equal(fixture.localTests.has('chef-b:printer-1'), false);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal((await fixture.store.listOperations('chef-b')).length, 0);
});

test('changing accounts during a physical local test retains its pending recovery under the original account', {timeout: 5000}, async () => {
 const fixture = application({autoLocal: true}), entered = deferred(), gate = deferred();
 let original;
 fixture.client.localFetch = async (path, options = {}) => {
  if (path === '/v1/session') return detectedUsbSession();
  original = JSON.parse(options.body);
  entered.resolve();
  return gate.promise;
 };
 const pending = fixture.client.testLocal(fixture.data.printers[0]).catch(error => error);
 await entered.promise;
 fixture.switchAccount();
 gate.resolve({idempotencyKey: original.idempotencyKey, status: 'Printed'});
 const error = await pending;

 assert.equal(error.code, 'ACCOUNT_CHANGED');
 assert.deepEqual(fixture.localTests.get('chef-a:printer-1'), original);
 assert.equal(fixture.localTests.has('chef-b:printer-1'), false);
 assert.equal(fixture.networkCalls.length, 0);
});

test('a repeated local token rejection cannot refresh and retry indefinitely', async () => {
 const fixture = application({autoLocal: true});
 let sessionCalls = 0, printCalls = 0;
 fixture.client.localFetch = async path => {
  if (path === '/v1/session') { sessionCalls++; return detectedUsbSession(); }
  assert.equal(path, '/v1/test-print');
  printCalls++;
  throw Object.assign(Error('Local token expired'), {status: 401});
 };

 await assert.rejects(fixture.client.testLocal(fixture.data.printers[0]), error => error.status === 401);

 assert.equal(sessionCalls, 2);
 assert.equal(printCalls, 2);
 assert.equal(fixture.networkCalls.length, 0);
 assert.equal(fixture.localTests.has('chef-a:printer-1'), true);
});
