/* Durable, per-account offline state. A write resolves only after its transaction commits. */
(function (root) {
 'use strict';

 const DATABASE = 'kivra-offline';
 const VERSION = 1;
 const LEASE_MS = 120000;
 const RENEW_MS = 20000;
 let opening;

 function storageError(cause) {
  const error = new Error('Offline storage is unavailable. Keep this page open and reconnect before saving.');
  error.code = 'OFFLINE_STORAGE_UNAVAILABLE';
  if (cause) error.cause = cause;
  return error;
 }

 function clone(value) {
  return typeof root.structuredClone === 'function' ? root.structuredClone(value) : JSON.parse(JSON.stringify(value));
 }

 function account(userId) {
  if (typeof userId !== 'string' || !userId.trim()) throw new Error('A signed-in account is required for offline storage.');
  return userId;
 }

 function hasCredentials(value, seen = new Set()) {
  if (!value || typeof value !== 'object' || seen.has(value)) return false;
  seen.add(value);
  return Object.entries(value).some(([key, item]) => /^(pin|password|newPin|oldPin|currentPin|confirmPin)$/i.test(key) || hasCredentials(item, seen));
 }

 function openDatabase() {
  if (opening) return opening;
  opening = new Promise((resolve, reject) => {
   let request, settled = false;
   const fail = cause => { if (!settled) { settled = true; reject(storageError(cause)); } };
   try {
    if (!root.indexedDB) { fail(); return; }
    request = root.indexedDB.open(DATABASE, VERSION);
    request.onupgradeneeded = () => {
     const db = request.result;
     if (!db.objectStoreNames.contains('session')) db.createObjectStore('session', {keyPath: 'key'});
     if (!db.objectStoreNames.contains('bootstrap')) db.createObjectStore('bootstrap', {keyPath: 'userId'});
     if (!db.objectStoreNames.contains('operations')) {
      const store = db.createObjectStore('operations', {keyPath: ['userId', 'id']});
      store.createIndex('userId', 'userId', {unique: false});
     }
     if (!db.objectStoreNames.contains('metadata')) db.createObjectStore('metadata', {keyPath: 'key'});
     if (!db.objectStoreNames.contains('leases')) db.createObjectStore('leases', {keyPath: 'userId'});
    };
    request.onerror = () => fail(request.error);
    request.onblocked = () => fail(new Error('Close other KIVRA tabs to upgrade offline storage.'));
    request.onsuccess = () => {
     const db = request.result;
     if (settled) { db.close(); return; }
     settled = true;
     db.onversionchange = () => { db.close(); opening = undefined; };
     resolve(db);
    };
   } catch (error) { fail(error); }
  }).catch(error => { opening = undefined; throw error; });
  return opening;
 }

 function result(request) {
  return new Promise((resolve, reject) => {
   request.onsuccess = () => resolve(request.result);
   request.onerror = () => reject(request.error || new Error('Offline storage request failed.'));
  });
 }

 async function transact(stores, mode, callback) {
  const db = await openDatabase();
  let transaction;
  try { transaction = db.transaction(stores, mode); }
  catch (error) { opening = undefined; throw storageError(error); }
  const committed = new Promise((resolve, reject) => {
   transaction.oncomplete = resolve;
   transaction.onabort = () => reject(transaction.error || new Error('Offline storage transaction was cancelled.'));
   transaction.onerror = () => {}; // onabort reports the final outcome, including commit failures.
  });
  // A request may fail before callback finishes; attach the rejection handler immediately.
  committed.catch(() => {});
  try {
   const value = await callback(transaction);
   await committed;
   return value;
  } catch (error) {
   try { transaction.abort(); } catch (_) {}
   await committed.catch(() => {});
   throw storageError(error);
  }
 }

 async function readSession() {
  const record = await transact(['session'], 'readonly', tx => result(tx.objectStore('session').get('active')));
  if (!record) return null;
  const expiresAt = typeof record.value.expiresAt === 'number' ? record.value.expiresAt : Date.parse(record.value.expiresAt);
  return Number.isFinite(expiresAt) && expiresAt > Date.now() ? clone(record.value) : null;
 }

 async function saveSession(session) {
  const expiresAt = typeof session?.expiresAt === 'number' ? session.expiresAt : Date.parse(session?.expiresAt);
  if (!session?.token || !session?.user?.id || !Number.isFinite(expiresAt)) throw new Error('A valid session with an expiry is required.');
  if (hasCredentials(session.user)) throw new Error('PINs and passwords cannot be saved for offline use.');
  const value = clone({token: session.token, user: session.user, expiresAt: session.expiresAt});
  await transact(['session'], 'readwrite', tx => result(tx.objectStore('session').put({key: 'active', value})));
 }

 async function clearSession() {
  await transact(['session'], 'readwrite', tx => result(tx.objectStore('session').delete('active')));
 }

 async function readBootstrap(userId) {
  account(userId);
  const record = await transact(['bootstrap'], 'readonly', tx => result(tx.objectStore('bootstrap').get(userId)));
  return record ? clone(record.data) : null;
 }

 async function saveBootstrap(userId, data) {
  account(userId);
  if (hasCredentials(data)) throw new Error('PINs and passwords cannot be saved for offline use.');
  const record = {userId, data: clone(data)};
  await transact(['bootstrap'], 'readwrite', tx => result(tx.objectStore('bootstrap').put(record)));
 }

 async function listOperations(userId) {
  account(userId);
  const records = await transact(['operations'], 'readonly', tx => result(tx.objectStore('operations').index('userId').getAll(userId)));
  records.sort((a, b) => a.createdAt - b.createdAt || a.sequence - b.sequence || a.id.localeCompare(b.id));
  return records.map(record => clone(record.operation));
 }

 async function putOperation(userId, operation) {
  account(userId);
  const createdAt = typeof operation?.createdAt === 'number' ? operation.createdAt : Date.parse(operation?.createdAt);
  if (typeof operation?.id !== 'string' || !operation.id || !Number.isFinite(createdAt)) throw new Error('An offline operation needs an ID and creation time.');
  if (typeof operation.path !== 'string' || !operation.path.startsWith('/api/') || !/^(POST|PUT|PATCH|DELETE)$/i.test(operation.method || '')) throw new Error('Only application changes can be queued offline.');
  let body = operation.body;
  if (typeof body === 'string') { try { body = JSON.parse(body); } catch (_) {} }
  if (/^\/api\/auth(?:\/|\?|$)/i.test(operation.path) || hasCredentials(body)) throw new Error('Sign-in and PIN changes require a connection and cannot be queued.');
  const value = clone(operation);
  await transact(['operations', 'metadata'], 'readwrite', async tx => {
   const store = tx.objectStore('operations'), metadata = tx.objectStore('metadata');
   const existing = await result(store.get([userId, value.id]));
   let sequence = existing?.sequence;
   if (sequence === undefined) {
    const key = `sequence:${userId}`, counter = await result(metadata.get(key));
    sequence = (counter?.value || 0) + 1;
    await result(metadata.put({key, value: sequence}));
   }
   await result(store.put({userId, id: value.id, createdAt, sequence, operation: value}));
  });
  return clone(value);
 }

 async function removeOperation(userId, id) {
  account(userId);
  await transact(['operations'], 'readwrite', tx => result(tx.objectStore('operations').delete([userId, id])));
 }

 async function clearUser(userId) {
  account(userId);
  // Signing out must not discard changes that have not reached the server.
  await transact(['bootstrap'], 'readwrite', tx => result(tx.objectStore('bootstrap').delete(userId)));
 }

 function leaseOwner() {
  return root.crypto?.randomUUID ? root.crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
 }

 async function updateLease(userId, owner, release = false) {
  return transact(['leases'], 'readwrite', async tx => {
   const store = tx.objectStore('leases'), lease = await result(store.get(userId)), now = Date.now();
   if (lease && lease.owner !== owner && lease.expiresAt > now) return false;
   if (release) {
    if (lease?.owner === owner) await result(store.delete(userId));
   } else await result(store.put({userId, owner, expiresAt: now + LEASE_MS}));
   return true;
  });
 }

 async function withSyncLock(userId, callback) {
  account(userId);
  if (typeof callback !== 'function') throw new TypeError('A synchronization callback is required.');
  const locks = root.navigator?.locks;
  if (locks?.request) return locks.request(`kivra-offline-sync:${userId}`, {mode: 'exclusive'}, () => callback());

  // IndexedDB serializes competing lease transactions across tabs. Expiry lets a new tab
  // recover after a crashed tab. Server-side idempotency remains necessary after suspension.
  const owner = leaseOwner(), controller = typeof root.AbortController === 'function' ? new root.AbortController() : null;
  while (!await updateLease(userId, owner)) await new Promise(resolve => root.setTimeout(resolve, 200));
  let renewing = Promise.resolve(), lost;
  const timer = root.setInterval(() => {
   renewing = renewing.then(async () => {
    if (lost) return;
    try {
     if (!await updateLease(userId, owner)) throw new Error('Another page owns offline synchronization.');
    } catch (error) { lost = error; controller?.abort(); }
   });
  }, RENEW_MS);
  try {
   const value = await callback(controller?.signal);
   if (lost) throw lost;
   return value;
  } finally {
   root.clearInterval(timer);
   await renewing;
   await updateLease(userId, owner, true);
  }
 }

 root.KivraOffline = Object.freeze({readSession, saveSession, clearSession, readBootstrap, saveBootstrap, listOperations, putOperation, removeOperation, clearUser, withSyncLock});
})(globalThis);
