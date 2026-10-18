const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {resolve} = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../src/Kivra.Api/wwwroot/offline-store.js'), 'utf8');

// Small transactional IndexedDB test double: requests succeed before commit, writes
// are isolated until commit, abort rolls them back, and transactions serialize.
function indexedDatabase() {
 const databases = new Map();
 const control = {failCommit: false, holdCommit: false, waiting: [], requestsSucceeded: 0};
 const key = value => JSON.stringify(value);
 const copy = value => value === undefined ? undefined : structuredClone(value);

 function database() {
  const schemas = new Map(), values = new Map(), queue = [];
  let active = false;
  const db = {
   objectStoreNames: {contains: name => schemas.has(name)},
   createObjectStore(name, options) {
    const schema = {keyPath: options.keyPath, indexes: new Map()};
    schemas.set(name, schema);
    values.set(name, new Map());
    return {createIndex: (indexName, indexKey) => schema.indexes.set(indexName, indexKey)};
   },
   close() {},
   transaction(names, mode) {
    let pending = 0, started = false, ended = false, completion;
    const jobs = [], working = new Map();
    const tx = {
     error: null,
     objectStore(name) {
      assert.ok(names.includes(name), 'store must be included in the transaction');
      const schema = schemas.get(name);
      const request = operation => {
       if (ended) throw Error('TransactionInactiveError');
       const req = {};
       pending++;
       if (completion) { clearImmediate(completion); completion = undefined; }
       const execute = () => queueMicrotask(() => {
        if (ended) { req.error = Error('AbortError'); req.onerror?.(); return; }
        try { req.result = copy(operation(working.get(name))); }
        catch (error) { req.error = error; req.onerror?.(); abort(error); return; }
        control.requestsSucceeded++;
        req.onsuccess?.();
        pending--;
        maybeComplete();
       });
       if (started) execute(); else jobs.push(execute);
       return req;
      };
      const recordKey = value => Array.isArray(schema.keyPath) ? schema.keyPath.map(field => value[field]) : value[schema.keyPath];
      return {
       get: id => request(records => records.get(key(id))),
       put: value => request(records => {
        assert.equal(mode, 'readwrite');
        records.set(key(recordKey(value)), copy(value));
        return recordKey(value);
       }),
       delete: id => request(records => {
        assert.equal(mode, 'readwrite');
        records.delete(key(id));
       }),
       index: indexName => ({getAll: id => request(records => {
        const field = schema.indexes.get(indexName);
        assert.ok(field);
        return [...records.values()].filter(value => value[field] === id);
       })})
      };
     },
     abort: () => abort(Error('AbortError'))
    };
    function release() { active = false; startNext(); }
    function abort(error) {
     if (ended) throw Error('TransactionInactiveError');
     ended = true;
     if (completion) clearImmediate(completion);
     tx.error = error;
     queueMicrotask(() => { tx.onabort?.(); release(); });
    }
    function maybeComplete() {
     if (!started || ended || pending || completion) return;
     completion = setImmediate(() => {
      completion = undefined;
      const finish = () => {
       if (ended) return;
       if (control.failCommit && mode === 'readwrite') {
        control.failCommit = false;
        abort(Error('QuotaExceededError: transaction commit failed'));
        return;
       }
       ended = true;
       if (mode === 'readwrite') for (const [name, records] of working) values.set(name, records);
       tx.oncomplete?.();
       release();
      };
      if (control.holdCommit && mode === 'readwrite') control.waiting.push(finish);
      else finish();
     });
    }
    queue.push(() => {
     started = true;
     for (const name of names) working.set(name, copyMap(values.get(name)));
     for (const job of jobs) job();
     maybeComplete();
    });
    queueMicrotask(startNext);
    return tx;
   }
  };
  function copyMap(map) { return new Map([...map].map(([id, value]) => [id, copy(value)])); }
  function startNext() {
   if (active || !queue.length) return;
   active = true;
   queue.shift()();
  }
  return db;
 }

 const indexedDB = {open(name) {
  const request = {};
  queueMicrotask(() => {
   const fresh = !databases.has(name);
   if (fresh) databases.set(name, database());
   request.result = databases.get(name);
   if (fresh) request.onupgradeneeded?.();
   request.onsuccess?.();
  });
  return request;
 }};
 return {indexedDB, control};
}

function application(options = {}) {
 const context = vm.createContext({
  indexedDB: options.indexedDB,
  navigator: options.navigator || {},
  structuredClone,
  setTimeout, setInterval, clearInterval,
  AbortController,
  ...options.context
 });
 vm.runInContext(source, context);
 return context.KivraOffline;
}

function operation(id, extra = {}) {
 return {id, path: '/api/items', method: 'POST', body: {name: 'Rice'}, createdAt: '2026-10-18T10:00:00Z', status: 'pending', ...extra};
}

test('session and bootstrap survive a new page instance and are scoped to the account', async () => {
 const {indexedDB} = indexedDatabase();
 const first = application({indexedDB});
 const session = {token: 'token', user: {id: 'chef-a', displayName: 'Chef'}, expiresAt: Date.now() + 60000};
 await first.saveSession(session);
 await first.saveBootstrap('chef-a', {items: [{id: 'rice'}]});
 await first.saveBootstrap('chef-b', {items: [{id: 'pasta'}]});
 const reopened = application({indexedDB});
 assert.deepEqual(await reopened.readSession(), session);
 assert.deepEqual(await reopened.readBootstrap('chef-a'), {items: [{id: 'rice'}]});
 assert.deepEqual(await reopened.readBootstrap('chef-b'), {items: [{id: 'pasta'}]});
 assert.equal(await reopened.readBootstrap('unknown'), null);
 await reopened.clearSession();
 assert.equal(await first.readSession(), null);
});

test('expired sessions cannot restore offline access', async () => {
 const {indexedDB} = indexedDatabase();
 const store = application({indexedDB});
 await store.saveSession({token: 'old', user: {id: 'chef'}, expiresAt: new Date(Date.now() - 1).toISOString()});
 assert.equal(await store.readSession(), null);
 await assert.rejects(store.saveSession({token: 'old', user: {id: 'chef'}, expiresAt: 'invalid'}), /expiry/);
});

test('operations replay by creation time and stable insertion order, including after an update', async () => {
 const {indexedDB} = indexedDatabase();
 const store = application({indexedDB});
 await store.putOperation('chef', operation('z-last-alphabetically'));
 await store.putOperation('chef', operation('a-first-alphabetically'));
 await store.putOperation('chef', operation('older', {createdAt: '2026-10-18T09:00:00Z'}));
 await store.putOperation('chef', operation('z-last-alphabetically', {status: 'failed', error: 'Conflict'}));
 const records = await store.listOperations('chef');
 assert.deepEqual(Array.from(records, entry => entry.id), ['older', 'z-last-alphabetically', 'a-first-alphabetically']);
 assert.equal(records[1].status, 'failed');
 assert.equal(records[1].error, 'Conflict');
 assert.equal(Object.hasOwn(records[1], 'sequence'), false);
});

test('a caller cannot mutate persisted bootstrap or operations through shared references', async () => {
 const {indexedDB} = indexedDatabase();
 const store = application({indexedDB}), data = {items: [{name: 'Rice'}]}, change = operation('one');
 const saving = store.saveBootstrap('chef', data);
 data.items[0].name = 'Changed';
 await saving;
 await store.putOperation('chef', change);
 change.body.name = 'Changed';
 const returned = await store.listOperations('chef');
 returned[0].body.name = 'Changed again';
 assert.equal((await store.readBootstrap('chef')).items[0].name, 'Rice');
 assert.equal((await store.listOperations('chef'))[0].body.name, 'Rice');
});

test('clearing an account cache and session retains pending work and isolates other accounts', async () => {
 const {indexedDB} = indexedDatabase();
 const store = application({indexedDB});
 await store.saveBootstrap('chef-a', {items: []});
 await store.saveBootstrap('chef-b', {items: [{id: 'pasta'}]});
 await store.putOperation('chef-a', operation('same-id'));
 await store.putOperation('chef-b', operation('same-id', {body: {name: 'Pasta'}}));
 await store.clearUser('chef-a');
 await store.clearSession();
 assert.equal(await store.readBootstrap('chef-a'), null);
 assert.deepEqual(await store.readBootstrap('chef-b'), {items: [{id: 'pasta'}]});
 assert.equal((await store.listOperations('chef-a')).length, 1);
 await store.removeOperation('chef-a', 'same-id');
 assert.equal((await store.listOperations('chef-a')).length, 0);
 assert.equal((await store.listOperations('chef-b'))[0].body.name, 'Pasta');
});

test('offline writes remain pending until the transaction commits', async () => {
 const {indexedDB, control} = indexedDatabase();
 const store = application({indexedDB});
 control.holdCommit = true;
 let finished = false;
 const pending = store.putOperation('chef', operation('one')).then(() => { finished = true; });
 while (!control.waiting.length) await new Promise(resolve => setImmediate(resolve));
 assert.ok(control.requestsSucceeded > 0);
 assert.equal(finished, false);
 control.holdCommit = false;
 control.waiting.shift()();
 await pending;
 assert.equal(finished, true);
 assert.equal((await store.listOperations('chef')).length, 1);
});

test('an abort after successful requests rejects saving and rolls back the operation', async () => {
 const {indexedDB, control} = indexedDatabase();
 const store = application({indexedDB});
 control.failCommit = true;
 await assert.rejects(store.putOperation('chef', operation('lost')), error => error.code === 'OFFLINE_STORAGE_UNAVAILABLE');
 assert.equal((await store.listOperations('chef')).length, 0);
 await store.putOperation('chef', operation('saved'));
 assert.equal((await store.listOperations('chef'))[0].id, 'saved');
});

test('unavailable or blocked IndexedDB never reports that a change was saved', async () => {
 const store = application();
 await assert.rejects(store.putOperation('chef', operation('one')), error => error.code === 'OFFLINE_STORAGE_UNAVAILABLE');
 await assert.rejects(store.saveSession({token: 'x', user: {id: 'chef'}, expiresAt: Date.now() + 60000}), /Offline storage is unavailable/);
 await assert.rejects(store.readBootstrap('chef'), /Offline storage is unavailable/);
 let invoked = false;
 await assert.rejects(store.withSyncLock('chef', () => { invoked = true; }), /Offline storage is unavailable/);
 assert.equal(invoked, false);
 const blocked = application({indexedDB: {open() { const request = {}; queueMicrotask(() => request.onblocked()); return request; }}});
 await assert.rejects(blocked.saveBootstrap('chef', {items: []}), /Offline storage is unavailable/);
});

test('PIN credentials and sign-in requests cannot enter durable offline storage', async () => {
 const {indexedDB} = indexedDatabase();
 const store = application({indexedDB});
 await assert.rejects(store.putOperation('chef', operation('auth', {path: '/api/auth/pin', body: '{}'})), /cannot be queued/);
 await assert.rejects(store.putOperation('chef', operation('user', {path: '/api/users', body: JSON.stringify({pin: '1234'})})), /cannot be queued/);
 await assert.rejects(store.putOperation('chef', operation('nested', {body: {user: {newPin: '1234'}}})), /cannot be queued/);
 await assert.rejects(store.saveSession({token: 'x', user: {id: 'chef', pin: '1234'}, expiresAt: Date.now() + 60000}), /cannot be saved/);
 await assert.rejects(store.saveBootstrap('chef', {user: {password: 'secret'}}), /cannot be saved/);
 assert.equal((await store.listOperations('chef')).length, 0);
});

test('Web Locks receives an exclusive per-account name and returns callback result', async () => {
 const calls = [];
 const store = application({navigator: {locks: {request: async (name, options, callback) => {
  calls.push({name, mode: options.mode});
  return callback();
 }}}});
 assert.equal(await store.withSyncLock('chef-a', async () => 'done'), 'done');
 assert.deepEqual(calls, [{name: 'kivra-offline-sync:chef-a', mode: 'exclusive'}]);
});

test('the lease fallback serializes competing tabs and releases after failure', async () => {
 const {indexedDB} = indexedDatabase();
 const first = application({indexedDB}), second = application({indexedDB}), events = [];
 let release, entered;
 const started = new Promise(resolve => { entered = resolve; });
 const held = first.withSyncLock('chef', async () => {
  events.push('first');
  entered();
  await new Promise(resolve => { release = resolve; });
  events.push('released');
 });
 await started;
 const waiting = second.withSyncLock('chef', async () => { events.push('second'); return 'done'; });
 await new Promise(resolve => setTimeout(resolve, 20));
 assert.deepEqual(events, ['first']);
 release();
 await held;
 assert.equal(await waiting, 'done');
 assert.deepEqual(events, ['first', 'released', 'second']);
 await assert.rejects(first.withSyncLock('chef', async () => { throw Error('network failed'); }), /network failed/);
 assert.equal(await second.withSyncLock('chef', async () => 'retry'), 'retry');
});

test('different accounts do not block each other during lease-based synchronization', async () => {
 const {indexedDB} = indexedDatabase();
 const first = application({indexedDB}), second = application({indexedDB});
 let release, entered;
 const started = new Promise(resolve => { entered = resolve; });
 const held = first.withSyncLock('chef-a', async () => {
  entered();
  await new Promise(resolve => { release = resolve; });
 });
 await started;
 assert.equal(await second.withSyncLock('chef-b', async () => 'independent'), 'independent');
 release();
 await held;
});
