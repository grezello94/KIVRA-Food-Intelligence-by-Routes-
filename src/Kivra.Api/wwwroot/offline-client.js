/* Durable, ordered offline work. Authentication and device discovery always use the server. */
(() => {
  'use strict';
  const copy = value => JSON.parse(JSON.stringify(value));
  const transient = error => error.network === true || [408, 425, 429].includes(error.status) || error.status >= 500;
  const configWrite = (path, method) =>
    (method === 'POST' && /^\/api\/(items|categories|storage-locations|printers)$/.test(path)) ||
    (method === 'PUT' && /^\/api\/(items|printers)\/[^/]+$/.test(path)) ||
    (method === 'PATCH' && /^\/api\/(items\/[^/]+\/image|storage-locations\/[^/]+\/active|labels\/[^/]+\/status)$/.test(path));

  class OfflineClient {
    constructor(options) {
      Object.assign(this, options);
      this.status = { online: true, syncing: false, pending: 0, blocked: 0, storageError: '', lastSyncedAt: null };
      this.chain = Promise.resolve();
      this.localSession = null;
      this.localTests = new Map();
      this.syncRun = null;
    }
    userId() { return this.getSession().user?.id; }
    owner() {
      const session = this.getSession();
      return { userId: session.user?.id, token: session.token || '', user: session.user ? copy(session.user) : null };
    }
    isCurrent(owner) { return this.userId() === owner.userId && (this.getSession().token || '') === owner.token; }
    requireOwner(owner) {
      if (!this.isCurrent(owner)) throw Object.assign(Error('The signed-in account changed. Your saved work is retained.'), { code: 'ACCOUNT_CHANGED' });
      if (owner.signal?.aborted) throw Object.assign(Error('Offline synchronization moved to another page. Your saved work is retained.'), { network: true });
    }
    emitFor(owner, values) { if (this.isCurrent(owner)) this.emit(values); }
    serverRequest(path, options, owner) {
      this.requireOwner(owner);
      return this.network(path, { ...options, ...(owner.signal ? { signal: owner.signal } : {}), headers: { ...options.headers, Authorization: owner.token ? `Bearer ${owner.token}` : '' } });
    }
    emit(values = {}) { Object.assign(this.status, values); this.onStatus?.({ ...this.status }); }
    async operations(owner = this.owner()) { return this.store.listOperations(owner.userId); }
    async updateStatus(owner = this.owner()) {
      const operations = await this.operations(owner);
      this.emitFor(owner, { pending: operations.length, blocked: operations.filter(x => x.status === 'blocked').length });
    }
    async initialize() {
      const owner = this.owner();
      try {
        await this.updateStatus(owner);
        const cached = await this.store.readBootstrap(owner.userId);
        return this.isCurrent(owner) ? cached : null;
      } catch (error) { this.emitFor(owner, { storageError: error.message }); return null; }
    }
    async persist(owner = this.owner()) {
      this.requireOwner(owner);
      const data = copy(this.getSnapshot());
      try { await this.store.saveBootstrap(owner.userId, data); }
      catch (error) { this.emitFor(owner, { storageError: error.message }); throw error; }
    }
    localRead(path) {
      const data = this.getSnapshot();
      if (!this.hasSnapshot()) return undefined;
      if (path === '/api/bootstrap') return copy(data);
      if (path.startsWith('/api/labels?') || path === '/api/labels') return copy(data.labels || []);
      const names = { '/api/items': 'items', '/api/categories': 'categories', '/api/printers': 'printers', '/api/printers/all': 'allPrinters', '/api/storage-locations': 'locations', '/api/storage-locations/all': 'allLocations', '/api/users': 'users', '/api/bridge/devices': 'bridgeDevices' };
      if (names[path]) return copy(data[names[path]] || []);
      return undefined;
    }
    async request(path, options = {}) {
      const owner = this.owner();
      options = { ...options, headers: { ...options.headers } };
      const method = (options.method || 'GET').toUpperCase();
      if (method === 'GET') {
        // Pending local changes must stay visible until their entire ordered queue is synced.
        if (!this.status.online || this.status.pending) {
          const local = this.localRead(path);
          if (local !== undefined) return local;
        }
        try {
          const response = await this.serverRequest(path, options, owner);
          this.requireOwner(owner);
          this.emitFor(owner, { online: true });
          return response;
        } catch (error) {
          this.requireOwner(owner);
          if (!transient(error)) throw error;
          this.emitFor(owner, { online: false });
          const local = this.localRead(path);
          if (local !== undefined) return local;
          throw error;
        }
      }
      const print = path === '/api/labels/print';
      const reprint = /^\/api\/labels\/[^/]+\/reprint$/.test(path);
      if (!configWrite(path, method) && !print && !reprint) {
        if (!this.status.online) throw Error('This action needs a connection to the KIVRA server. Your saved kitchen work is available offline.');
        const response = await this.serverRequest(path, options, owner);
        this.requireOwner(owner);
        return response;
      }
      const task = () => this.mutate(path, method, options, { print, reprint }, owner);
      const result = this.chain.then(task, task);
      this.chain = result.catch(() => {});
      return result;
    }
    async mutate(path, method, options, kind, owner = this.owner()) {
      this.requireOwner(owner);
      const session = owner, userId = owner.userId;
      if (!userId) throw Error('Sign in before saving offline work.');
      if (configWrite(path, method) && !path.includes('/labels/') && session.user.role !== 'Administrator') throw Error('Administrator access is required.');
      if ((kind.reprint || path.endsWith('/status')) && !['Administrator', 'Supervisor'].includes(session.user.role)) throw Error('Supervisor or Administrator access is required.');
      const body = options.body ? JSON.parse(options.body) : {};
      const id = body.idempotencyKey || this.newId();
      if (kind.print || kind.reprint) body.idempotencyKey = id;
      if (method === 'POST' && /^\/api\/(items|categories|storage-locations|printers)$/.test(path)) body.id ||= this.newId();
      if (kind.print) body.operationalDateTime ||= new Date().toISOString();
      let operation = { id, path, method, body: JSON.stringify(body), createdAt: new Date().toISOString(), status: 'pending', type: 'server' };
      if (kind.print && await this.prepareLocal(body.printerId, owner)) {
        operation.type = 'local-print';
        operation.localRequest = this.printSnapshot(body, id);
      }
      if (kind.reprint) {
        const label = this.getSnapshot().labels?.find(x => x.id === path.split('/')[3]);
        if (label && await this.prepareLocal(label.printerId, owner)) { operation.type = 'local-print'; operation.localRequest = this.reprintSnapshot(label, id); }
      }
      if (configWrite(path, method) && method !== 'POST') {
        const field = { items: 'items', printers: 'allPrinters', 'storage-locations': 'allLocations', labels: 'labels' }[path.split('/')[2]];
        operation.previousEntity = copy((this.getSnapshot()[field] || []).find(x => x.id === path.split('/')[3]) || null);
      }
      const previous = await this.operations(owner);
      this.requireOwner(owner);
      // Commit before any network or spooler I/O. A closed tab can always recover this request.
      await this.store.putOperation(userId, operation);
      await this.updateStatus(owner);
      this.requireOwner(owner);
      if (operation.type === 'local-print') {
        let result;
        try {
          result = await this.sendLocal(operation, owner);
        } catch (error) {
          if (error.code === 'ACCOUNT_CHANGED') throw error;
          operation.error = error.message;
          await this.store.putOperation(userId, operation);
          this.requireOwner(owner);
          await this.updateStatus(owner);
          this.applyOptimistic(operation, undefined, owner);
          await this.persist(owner);
          return this.pendingResult(operation);
        }
        await this.updateStatus(owner);
        this.requireOwner(owner);
        return result;
      }
      if (this.status.online && !previous.length) {
        try {
          const response = await this.sendServer(operation, owner);
          await this.store.removeOperation(userId, id);
          await this.updateStatus(owner);
          this.requireOwner(owner);
          return response;
        } catch (error) {
          if (error.code === 'ACCOUNT_CHANGED') throw error;
          if (!transient(error)) {
            if (error.status === 401) { operation.error = 'Sign in again to sync this saved request.'; await this.store.putOperation(userId, operation); }
            else await this.store.removeOperation(userId, id);
            await this.updateStatus(owner);
            throw error;
          }
          this.emitFor(owner, { online: false });
        }
      }
      this.requireOwner(owner);
      this.applyOptimistic(operation, undefined, owner);
      await this.persist(owner);
      await this.updateStatus(owner);
      this.requireOwner(owner);
      return this.pendingResult(operation);
    }
    pendingResult(operation) {
      if (operation.path === '/api/labels/offline-import') {
        const receipt = JSON.parse(operation.body);
        return { status: 'Printed', labelId: receipt.labelId, labelCode: receipt.labelCode, offlinePending: true, locallyPrinted: true, idempotencyKey: operation.id };
      }
      if (operation.path === '/api/labels/print' || operation.path.includes('/reprint')) {
        const body = JSON.parse(operation.body);
        const printUncertain = operation.type === 'local-print' && (operation.status === 'blocked' || !!operation.error);
        return { status: 'OfflinePending', labelId: operation.localRequest?.labelId || operation.id, labelCode: operation.localRequest?.labelCode || 'Pending sync', offlinePending: true, idempotencyKey: body.idempotencyKey, failureReason: operation.error, requiresReview: printUncertain, printUncertain };
      }
      return { ...JSON.parse(operation.body), offlinePending: true };
    }
    applyOptimistic(operation, result, owner = this.owner()) {
      if (!this.isCurrent(owner)) return;
      const data = copy(this.getSnapshot()), body = JSON.parse(operation.body), parts = operation.path.split('/'), collection = parts[2], id = parts[3];
      const upsert = (name, value) => { data[name] ||= []; const index = data[name].findIndex(x => x.id === value.id); if (index < 0) data[name].push(value); else data[name][index] = { ...data[name][index], ...value }; };
      if (collection === 'labels') {
        if ((body.isReprint || operation.localRequest?.isReprint) && (operation.path.includes('/reprint') || operation.path === '/api/labels/offline-import')) {
          const label = data.labels?.find(x => x.id === (body.labelId || operation.localRequest?.labelId));
          if (label) {
            const keys = label.pendingReprintKeys || [];
            if (!keys.includes(operation.id) && result?.status === 'Printed') { label.successfulPrintCount = (label.successfulPrintCount || 0) + 1; keys.push(operation.id); }
            label.pendingReprintKeys = keys; label.lastPrintStatus = result?.status || 'OfflinePending'; label.offlinePending = true;
          }
        } else if (operation.path === '/api/labels/print' || operation.path === '/api/labels/offline-import') {
          const item = data.items?.find(x => x.id === body.itemId), location = data.locations?.find(x => x.id === body.storageLocationId);
          const local = operation.localRequest;
          const labelId = result?.labelId || local?.labelId || body.labelId || operation.id;
          const existing = (data.labels || []).find(x => x.id === labelId);
          upsert('labels', { ...existing, itemId: body.itemId || local?.itemId, printerId: body.printerId || local?.printerId, storageLocationId: body.storageLocationId || local?.storageLocationId, categorySnapshot: body.categorySnapshot || local?.categorySnapshot || item?.category, classificationSnapshot: body.classificationSnapshot || local?.classificationSnapshot || item?.classification, dateTerminologySnapshot: body.dateTerminologySnapshot || local?.dateTerminologySnapshot || item?.dateTerminology, shelfLifeRuleSnapshot: `${body.shelfLifeValue || local?.shelfLifeValue || item?.shelfLifeValue} ${body.shelfLifeUnit || local?.shelfLifeUnit || item?.shelfLifeUnit}`, id: labelId, labelCode: result?.labelCode || local?.labelCode || body.labelCode || 'Pending sync', itemName: body.itemNameSnapshot || item?.name || 'Kitchen item', storageLocation: body.storageLocationSnapshot || location?.name || '', operationalDateTime: result?.receipt?.operationalDateTime || body.operationalDateTime, expiryDateTime: result?.receipt?.expiryDateTime || body.expiryDateTime || this.previewExpiry(body.operationalDateTime, item, data.restaurantTimeZoneId), createdAt: operation.createdAt, status: 'Active', lastPrintStatus: result?.status || 'OfflinePending', successfulPrintCount: result?.status === 'Printed' ? Math.max(1, existing?.successfulPrintCount || 0) : (existing?.successfulPrintCount || 0), offlinePending: true });
        } else if (parts[4] === 'status') {
          const label = data.labels?.find(x => x.id === id);
          if (label) { label.status = body.status; label.offlinePending = true; }
        }
      } else {
        const field = { items: 'items', categories: 'categories', printers: 'allPrinters', 'storage-locations': 'allLocations' }[collection];
        if (field) {
          const value = { ...body, id: body.id || id, offlinePending: true };
          if (collection === 'items') { value.category = data.categories?.find(x => x.id === body.categoryId)?.name; value.storage = data.locations?.find(x => x.id === body.defaultStorageLocationId)?.name; }
          upsert(field, value);
          if (collection === 'printers') data.printers = data.allPrinters.filter(x => x.enabled);
          if (collection === 'storage-locations') data.locations = data.allLocations.filter(x => x.active);
        }
      }
      data.expiry = (data.labels || []).filter(x => ['Active', 'Expired'].includes(x.status)).map(x => ({ id: x.id, itemNameSnapshot: x.itemName, labelCode: x.labelCode, operationalDateTime: x.operationalDateTime, expiryDateTime: x.expiryDateTime, storageLocationSnapshot: x.storageLocation, currentStatus: x.status, offlinePending: x.offlinePending })).sort((a, b) => new Date(a.expiryDateTime) - new Date(b.expiryDateTime));
      this.setSnapshot(data);
    }
    printSnapshot(body, key) {
      const data = this.getSnapshot(), item = data.items.find(x => x.id === body.itemId), printer = data.printers.find(x => x.id === body.printerId), location = data.locations.find(x => x.id === body.storageLocationId);
      if (!item || !printer || !location) throw Error('Download the item, storage and printer settings before printing offline.');
      const labelId = this.newId();
      return { idempotencyKey: key, labelId, labelCode: `OFF-${labelId.replaceAll('-', '').slice(0, 12)}`, itemId: item.id, printerId: printer.id, storageLocationId: location.id, operationalDateTime: body.operationalDateTime, itemNameSnapshot: item.name, categorySnapshot: item.category || 'Uncategorized', classificationSnapshot: item.classification, dateTerminologySnapshot: item.dateTerminology, shelfLifeValue: item.shelfLifeValue, shelfLifeUnit: item.shelfLifeUnit, storageLocationSnapshot: location.name, restaurantTimeZoneId: data.restaurantTimeZoneId || 'Asia/Kolkata', printer: copy(printer) };
    }
    reprintSnapshot(label, key) {
      const data = this.getSnapshot(), printer = data.printers.find(x => x.id === label.printerId), rule = /^(\d+) (Minutes|Hours|Days|Weeks|Months)$/.exec(label.shelfLifeRuleSnapshot || '');
      if (!printer || !rule || !label.itemId || !label.storageLocationId || !label.dateTerminologySnapshot || !label.categorySnapshot || !label.classificationSnapshot) throw Error('Reconnect once to download this label’s original print details before reprinting offline.');
      return { idempotencyKey: key, labelId: label.id, labelCode: label.labelCode, itemId: label.itemId, printerId: printer.id, storageLocationId: label.storageLocationId, operationalDateTime: label.operationalDateTime, expiryDateTime: label.expiryDateTime, itemNameSnapshot: label.itemName, categorySnapshot: label.categorySnapshot, classificationSnapshot: label.classificationSnapshot, dateTerminologySnapshot: label.dateTerminologySnapshot, shelfLifeValue: Number(rule[1]), shelfLifeUnit: rule[2], storageLocationSnapshot: label.storageLocation, restaurantTimeZoneId: data.restaurantTimeZoneId || 'Asia/Kolkata', printer: copy(printer), isReprint: true };
    }
    async localFetch(path, options = {}) {
      const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 12000);
      try {
        const response = await fetch(`http://127.0.0.1:17847${path}`, { ...options, mode: 'cors', credentials: 'omit', targetAddressSpace: 'loopback', signal: controller.signal });
        const body = await response.json();
        if (!response.ok) throw Object.assign(Error(body.error || body.detail || 'The Windows print bridge rejected this request.'), { status: response.status });
        return body;
      } catch (error) {
        if (!error.status) {
          error.network = true;
          error.message = 'Cannot reach the printer service on this PC. Open KIVRA Print Bridge and allow local device access in this browser, then check the printer again.';
        }
        throw error;
      } finally { clearTimeout(timer); }
    }
    async connectLocal() { this.localSession = await this.localFetch('/v1/session'); return this.localSession; }
    async prepareLocal(printerId, owner = this.owner()) {
      this.requireOwner(owner);
      const printer = this.getSnapshot().printers?.find(x => x.id === printerId);
      if (!printer || printer.driver !== 'TscTsplUsb' || printer.enabled === false) return false;
      if (this.localPrinterId?.() === printerId) return true;
      if (!this.autoLocal) return false;
      let session;
      try { session = await this.connectLocal(); }
      catch (error) { this.requireOwner(owner); return !!this.requireLocal; }
      this.requireOwner(owner);
      if (!session.queueName || session.ready === false) return !!this.requireLocal;
      if (printer.usbQueueName && printer.usbQueueName.toLowerCase() !== session.queueName.toLowerCase()) return false;
      this.onLocalPrinter?.(printerId, session);
      return true;
    }
    async localAuthorized(path, options, owner = this.owner()) {
      this.requireOwner(owner);
      let session = this.localSession || await this.connectLocal();
      this.requireOwner(owner);
      const send = () => this.localFetch(path, { ...options, headers: { ...options?.headers, 'X-Kivra-Local-Token': session.token } });
      try { return await send(); }
      catch (error) {
        if (error.status !== 401) throw error;
        // A 401 rejects the request before spooler I/O. Only this definite
        // rejection can refresh the installation token and retry the same key.
        this.localSession = null;
        this.requireOwner(owner);
        session = await this.connectLocal();
        this.requireOwner(owner);
        return send();
      }
    }
    async selectLocalPrinter(queueName) {
      const owner = this.owner();
      const result = await this.localAuthorized('/v1/printer', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ queueName }) }, owner);
      this.requireOwner(owner);
      this.localSession = { ...this.localSession, ...result };
      return this.localSession;
    }
    async testLocal(printer) {
      const owner = this.owner();
      this.requireOwner(owner);
      if (printer.driver !== 'TscTsplUsb') throw Error('Select a TSC USB label printer for local printing.');
      const session = await this.connectLocal();
      this.requireOwner(owner);
      if (!session.capabilities?.includes('test-print')) throw Error('Update the printer service to 1.8.0 to test this PC’s printer directly.');
      const testId = `${owner.userId}:${printer.id}`;
      let body = this.localTests.get(testId) || this.loadLocalTest?.(printer.id);
      if (!body) {
        body = { idempotencyKey: this.newId(), printer: copy(printer), restaurantTimeZoneId: this.getSnapshot().restaurantTimeZoneId || 'Asia/Kolkata' };
        this.saveLocalTest?.(printer.id, body);
        this.localTests.set(testId, body);
      }
      try {
        const result = await this.localAuthorized('/v1/test-print', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }, owner);
        this.requireOwner(owner);
        if (result.status === 'Printed') { this.localTests.delete(testId); this.saveLocalTest?.(printer.id, null); }
        return result;
      } catch (error) {
        if (!error.status && error.code !== 'ACCOUNT_CHANGED') error.message = 'The test print response was interrupted. Check the physical label. Check again will recover this same test without printing another copy.';
        throw error;
      }
    }
    async sendLocal(operation, owner = this.owner()) {
      this.requireOwner(owner);
      const userId = owner.userId;
      let result;
      try { result = await this.localAuthorized('/v1/print', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(operation.localRequest) }, owner); }
      catch (error) { if (error.status === 401) this.localSession = null; throw error; }
      if (result.status !== 'Printed') {
        operation.status = 'blocked'; operation.error = result.failureReason || 'Check the printer before trying another label. Its last outcome is uncertain.';
        await this.store.putOperation(userId, operation);
        throw Error(operation.error);
      }
      const receipt = result.receipt;
      const original = operation.localRequest;
      const imported = { idempotencyKey: operation.id, labelId: original.labelId, labelCode: original.labelCode, itemId: original.itemId, printerId: original.printerId, storageLocationId: original.storageLocationId, itemNameSnapshot: original.itemNameSnapshot, categorySnapshot: original.categorySnapshot, classificationSnapshot: original.classificationSnapshot, dateTerminologySnapshot: original.dateTerminologySnapshot, shelfLifeValue: original.shelfLifeValue, shelfLifeUnit: original.shelfLifeUnit, storageLocationSnapshot: original.storageLocationSnapshot, operationalDateTime: receipt.operationalDateTime, expiryDateTime: receipt.expiryDateTime, printedAt: receipt.printedAt, payload: receipt.payload, isReprint: !!original.isReprint };
      operation.type = 'server'; operation.path = '/api/labels/offline-import'; operation.body = JSON.stringify(imported); operation.status = 'pending'; operation.error = null;
      // A physical print cannot be undone by signing out. Keep its receipt with the
      // originating account even if another account is now displaying this page.
      await this.store.putOperation(userId, operation);
      if (this.isCurrent(owner)) {
        this.applyOptimistic(operation, result, owner);
        await this.persist(owner);
      }
      return { ...result, offlinePending: true, locallyPrinted: true };
    }
    async sendServer(operation, owner = this.owner()) {
      return this.serverRequest(operation.path, { method: operation.method, body: operation.body, headers: { 'Idempotency-Key': operation.id } }, owner);
    }
    async sync({ retryBlocked = false } = {}) {
      const owner = this.owner(), userId = owner.userId;
      if (!userId || (this.syncRun && this.isCurrent(this.syncRun.owner))) return;
      const run = { owner };
      this.syncRun = run;
      this.emitFor(owner, { syncing: true });
      try {
        await this.store.withSyncLock(userId, async signal => {
          const syncOwner = { ...owner, signal };
          for (let operation of await this.store.listOperations(userId)) {
            if (!this.isCurrent(owner) || signal?.aborted) break;
            if (operation.status === 'blocked' && !retryBlocked) break;
            try {
              if (operation.type === 'local-print') {
                if (operation.status === 'blocked') break; // Never automatically repeat an uncertain physical print.
                await this.sendLocal(operation, syncOwner);
                operation = (await this.store.listOperations(userId)).find(x => x.id === operation.id);
                if (!this.isCurrent(owner) || signal?.aborted || !operation) break;
              }
              const response = await this.sendServer(operation, syncOwner);
              if (response?.status === 'Failed') throw Object.assign(Error(response.failureReason || 'Printing failed. Review this job.'), { status: 422 });
              await this.store.removeOperation(userId, operation.id);
              this.emitFor(owner, { online: true, lastSyncedAt: new Date().toISOString() });
            } catch (error) {
              if (error.code === 'ACCOUNT_CHANGED') break;
              operation.error = error.message;
              if (!transient(error) && error.status !== 401) operation.status = 'blocked';
              await this.store.putOperation(userId, operation);
              if (transient(error)) this.emitFor(owner, { online: false });
              break; // Preserve dependency order; a new category may precede a new item.
            }
          }
        });
        if (this.isCurrent(owner)) {
          await this.updateStatus(owner);
          if (this.isCurrent(owner) && !this.status.pending) await this.onSynced?.();
        }
      } catch (error) { this.emitFor(owner, { storageError: error.message }); }
      finally {
        if (this.syncRun === run) {
          this.syncRun = null;
          if (this.isCurrent(owner)) this.emit({ syncing: false });
          else this.status.syncing = false;
        }
      }
    }
    async discard(id) {
      const owner = this.owner(), operations = await this.operations(owner), operation = operations.find(x => x.id === id);
      this.requireOwner(owner);
      if (!operation || (operation.status !== 'blocked' && !configWrite(operation.path, operation.method))) return;
      // Do not discard locally printed receipts: they must remain available to reconcile history.
      if (operation.path === '/api/labels/offline-import') throw Error('This printed label must be synced. Keep its receipt and resolve the server error.');
      const body = JSON.parse(operation.body), entityId = (operation.method === 'POST' ? body.id : null) || (!operation.localRequest?.isReprint ? operation.localRequest?.labelId : null);
      if (entityId && operations.some(x => x.id !== id && (x.body.includes(entityId) || JSON.stringify(x.localRequest || {}).includes(entityId)))) throw Error('Other saved work depends on this request. Resolve or remove its dependent requests first.');
      if (operation.previousEntity) { const position = operations.findIndex(x => x.id === id); if (operations.slice(position + 1).some(x => x.path.split('/')[3] === operation.previousEntity.id)) throw Error('Remove the newest saved change for this record first.'); }
      await this.store.removeOperation(owner.userId, id);
      this.requireOwner(owner);
      const data = copy(this.getSnapshot());
      if (operation.method === 'POST' && /^\/api\/(items|categories|storage-locations|printers)$/.test(operation.path)) {
        const field = { items: 'items', categories: 'categories', 'storage-locations': 'allLocations', printers: 'allPrinters' }[operation.path.split('/')[2]];
        data[field] = (data[field] || []).filter(x => x.id !== body.id);
        if (field === 'allLocations') data.locations = data.allLocations.filter(x => x.active);
        if (field === 'allPrinters') data.printers = data.allPrinters.filter(x => x.enabled);
      } else if (operation.previousEntity) {
        const field = { items: 'items', printers: 'allPrinters', 'storage-locations': 'allLocations', labels: 'labels' }[operation.path.split('/')[2]];
        const index = (data[field] || []).findIndex(x => x.id === operation.previousEntity.id);
        if (index >= 0) data[field][index] = copy(operation.previousEntity);
        if (field === 'allLocations') data.locations = data.allLocations.filter(x => x.active);
        if (field === 'allPrinters') data.printers = data.allPrinters.filter(x => x.enabled);
      } else if (operation.type === 'local-print' && !operation.localRequest?.isReprint) {
        data.labels = (data.labels || []).filter(x => x.id !== operation.localRequest?.labelId);
        data.expiry = (data.expiry || []).filter(x => x.id !== operation.localRequest?.labelId);
      }
      if (operation.localRequest?.isReprint) { const label = data.labels?.find(x => x.id === operation.localRequest.labelId); if (label) { label.lastPrintStatus = 'Printed'; label.offlinePending = operations.some(x => x.id !== id && (JSON.parse(x.body).labelId === label.id || x.localRequest?.labelId === label.id)); } }
      data.expiry = (data.labels || []).filter(x => ['Active','Expired'].includes(x.status)).map(x => ({id:x.id,itemNameSnapshot:x.itemName,labelCode:x.labelCode,operationalDateTime:x.operationalDateTime,expiryDateTime:x.expiryDateTime,storageLocationSnapshot:x.storageLocation,currentStatus:x.status,offlinePending:x.offlinePending})).sort((a,b)=>new Date(a.expiryDateTime)-new Date(b.expiryDateTime));
      this.setSnapshot(data);
      await this.persist(owner);
      await this.updateStatus(owner);
    }
  }
  globalThis.KivraOfflineClient = OfflineClient;
})();
