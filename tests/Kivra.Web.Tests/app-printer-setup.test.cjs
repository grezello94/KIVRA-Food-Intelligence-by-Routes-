const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {resolve} = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../src/Kivra.Api/wwwroot/app.js'), 'utf8');
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return {promise, resolve}; };

function storage(initial = {}) {
 return {
  ...initial,
  getItem(key) { return Object.hasOwn(this, key) ? this[key] : null; },
  setItem(key, value) { this[key] = String(value); },
  removeItem(key) { delete this[key]; }
 };
}

function usbSession(overrides = {}) {
 return {
  token: 'installation-token', version: '1.8.0', deviceName: 'Kitchen PC',
  queueName: 'TSC TA220', ready: true,
  printers: [{name: 'TSC TA220', driverName: 'TSC TA220', compatible: true, ready: true}],
  capabilities: ['connection-test', 'test-print', 'printer-selection', 'cloud-registration'],
  ...overrides
 };
}

function application(options = {}) {
 const calls = [], localCalls = [], messages = [];
 const context = vm.createContext({
  sessionStorage: storage({kivraToken: 'session-a', kivraUser: JSON.stringify({id: 'admin-a', role: 'Administrator'})}),
  localStorage: options.localStorage || storage(),
  navigator: {platform: 'Win32', onLine: true}, location: {origin: 'https://kivra.example'},
  document: {
   querySelector: selector => selector === '#localUsbQueue' ? {value: context.selection || 'TSC TA220'} : {addEventListener() {}},
   documentElement: {classList: {add() {}}}
  },
  MutationObserver: class {observe() {}},
  render() { context.renders++; }, renders: 0,
  showToast(message, kind) { messages.push({message, kind}); },
  refreshBridgeDevices: async () => {},
  setTimeout, clearTimeout, AbortController, Uint8Array,
  fetch: async () => assert.fail('setup must use the controlled request functions')
 });
 vm.runInContext(source.slice(0, source.indexOf('function brandLogo')), context);
 const run = code => vm.runInContext(code, context);
 context.sessionReply = options.session || usbSession();
 context.client = {
  autoLocal: true, requireLocal: true,
  connectLocal: async () => { localCalls.push({path: '/v1/session'}); return context.sessionReply; },
  selectLocalPrinter: async name => { localCalls.push({path: '/v1/printer', name}); return {...await context.sessionReply, queueName: name}; },
  localAuthorized: async (path, request) => { localCalls.push({path, request: clone(request)}); return {success: true}; },
  localPrinterId: () => context.localStorage.kivraPrintMode === 'shared' ? '' : context.localStorage.kivraLocalPrinterId || ''
 };
 context.printers = options.printers || [];
 context.savedOffline = options.savedOffline || false;
 context.requestStub = async (path, request = {}) => {
  calls.push({path, request: clone(request)});
  return {...JSON.parse(request.body || '{}'), ...(context.savedOffline ? {offlinePending: true} : {})};
 };
 context.networkStub = async (path, request = {}) => {
  calls.push({path, request: clone(request)});
  if (path === '/api/bridge/pairing-code') return {code: '123456'};
  if (path === '/api/bridge/pair') return {token: 'cloud-bridge-token', deviceName: 'Registered kitchen PC'};
  return {success: true};
 };
 run("offlineClient=client;state.printers=JSON.parse(JSON.stringify(printers));state.allPrinters=state.printers.slice();state.view='home';localSetupReady=true;request=requestStub;networkRequest=networkStub");
 return {context, run, calls, localCalls, messages, button: {disabled: false}};
}

function configuredUsb(queue) {
 return {id: 'printer-a', name: 'Kitchen Printer', model: 'TSC TA220', driver: 'TscTsplUsb', enabled: true, usbQueueName: queue};
}

test('automatic setup never binds the only configured USB printer to a different explicit queue', async () => {
 const {context, run, calls} = application({printers: [configuredUsb('Other kitchen TSC')]});

 await run('discoverLocalPrinter({force:true})');

 assert.equal(context.localStorage.kivraLocalPrinterId, undefined);
 assert.equal(calls.length, 0);
});

for (const queue of [undefined, '', 'tsc ta220']) {
 test(`automatic setup binds a compatible single USB configuration with queue ${JSON.stringify(queue)}`, async () => {
  const {context, run, calls} = application({printers: [configuredUsb(queue)]});

  await run('discoverLocalPrinter({force:true})');

  assert.equal(context.localStorage.kivraLocalPrinterId, 'printer-a');
  assert.equal(calls.length, 0, 'existing compatible printer settings must be reused');
 });
}

for (const [queueName, driverName, expectedDpi] of [
 ['TSC TE310', 'TSC printer', 300],
 ['Kitchen label printer', 'TSC TE310', 300],
 ['TSC TE300', 'TSC printer', 300],
 ['Kitchen label printer', 'TSC TE210', 203]
]) {
 test(`automatic creation infers ${expectedDpi} DPI from ${queueName} / ${driverName}`, async () => {
  const session = usbSession({queueName, printers: [{name: queueName, driverName, compatible: true, ready: true}]});
  const {context, run, calls} = application({session});

  await run('discoverLocalPrinter({force:true})');

  assert.equal(calls.length, 1);
  assert.equal(calls[0].path, '/api/printers');
  assert.equal(calls[0].request.method, 'POST');
  const input = JSON.parse(calls[0].request.body);
  assert.equal(input.driver, 'TscTsplUsb');
  assert.equal(input.dpi, expectedDpi);
  assert.equal(input.usbQueueName, queueName);
  assert.ok(input.id);
  assert.ok(input.idempotencyKey);
  assert.equal(context.localStorage.kivraLocalPrinterId, input.id);
 });
}

test('automatic printer creation retains its draft ID and key through a failed save and reload', async () => {
 const first = application();
 first.context.requestStub = async (path, request) => {
  first.calls.push({path, request: clone(request)});
  throw Object.assign(Error('Server connection lost'), {network: true});
 };
 first.run('request=requestStub');
 await first.run('discoverLocalPrinter({force:true})');
 const original = JSON.parse(first.calls[0].request.body);

 const reloaded = application({localStorage: first.context.localStorage, savedOffline: true});
 await reloaded.run('discoverLocalPrinter({force:true})');

 const retry = JSON.parse(reloaded.calls[0].request.body);
 assert.equal(retry.id, original.id);
 assert.equal(retry.idempotencyKey, original.idempotencyKey);
 assert.equal(reloaded.context.localStorage.kivraLocalPrinterId, original.id);
 assert.equal(reloaded.run('state.printers[0].offlinePending'), true);
 assert.ok(reloaded.context.localStorage.getItem('kivraLocalPrinterDraft:admin-a:tsc ta220'));
});

test('a connection-only discovery before bootstrap does not postpone eligible automatic setup afterward', async () => {
 const {context, run, calls} = application();
 run('localSetupReady=false');
 await run('discoverLocalPrinter({create:false})');
 assert.equal(calls.length, 0);

 run('localSetupReady=true');
 await run('discoverLocalPrinter({create:true})');

 assert.equal(calls.length, 1, 'bootstrap completion should allow missing printer setup without waiting for the health cache to expire');
 assert.equal(calls[0].path, '/api/printers');
 assert.ok(context.localStorage.kivraLocalPrinterId);
});

for (const changed of ['account', 'client']) {
 test(`shared registration stops before cloud mutations when the ${changed} changes during local discovery`, async () => {
  const fixture = application(), pending = deferred();
  fixture.context.sessionReply = pending.promise;
  fixture.context.button = fixture.button;
  const registration = fixture.run('enableSharedPrinting(button)');
  if (changed === 'account') fixture.run("state.token='session-b';state.user={id:'admin-b',role:'Administrator'}");
  else fixture.run('offlineClient={...client}');
  pending.resolve(usbSession());
  await registration;

  assert.equal(fixture.calls.length, 0);
  assert.equal(fixture.localCalls.filter(call => call.path === '/v1/cloud').length, 0);
  assert.equal(fixture.messages.length, 0);
  assert.equal(fixture.button.disabled, false);
 });
}

for (const changed of ['account', 'client']) {
 test(`choosing a local printer cannot update setup after the ${changed} changes`, async () => {
  const fixture = application({printers: [configuredUsb('TSC TA220')]}), pending = deferred();
  fixture.context.client.selectLocalPrinter = async () => pending.promise;
  fixture.context.button = fixture.button;
  const choosing = fixture.run('chooseLocalPrinter(button)');
  if (changed === 'account') fixture.run("state.token='session-b';state.user={id:'admin-b',role:'Administrator'}");
  else fixture.run('offlineClient={...client}');
  pending.resolve(usbSession());
  await choosing;

  assert.equal(fixture.context.localStorage.kivraLocalPrinterId, undefined);
  assert.equal(fixture.run('localPrinterState'), null);
  assert.equal(fixture.localCalls.length, 0, 'a stale selection must not start another discovery');
  assert.equal(fixture.messages.length, 0);
  assert.equal(fixture.button.disabled, false);
 });
}

test('choosing shared printing unpins automatic local routing on another Windows PC', async () => {
 const fixture = application({printers: [configuredUsb('TSC TA220')]});
 fixture.context.localStorage.kivraLocalPrinterId = 'printer-a';
 fixture.context.button = fixture.button;

 await fixture.run('useSharedPrinter(button)');

 assert.equal(fixture.context.localStorage.kivraPrintMode, 'shared');
 assert.equal(fixture.context.client.autoLocal, false);
 assert.equal(fixture.context.client.requireLocal, false);
 assert.equal(fixture.context.client.localPrinterId(), '');
 assert.equal(fixture.run('usesLocalUsb()'), false);
});

test('a local selection finishing follow-up discovery cannot clear or render a new account’s printer state', {timeout: 5000}, async () => {
 const fixture = application({printers: [configuredUsb('TSC TA220')]}), entered = deferred(), pending = deferred();
 fixture.context.client.connectLocal = async () => { entered.resolve(); return pending.promise; };
 fixture.context.button = fixture.button;
 const choosing = fixture.run('chooseLocalPrinter(button)');
 await entered.promise;
 fixture.run("state.token='session-b';state.user={id:'admin-b',role:'Administrator'};state.view='printer-form';printerHealth.clear();printerHealth.set('b-printer',{token:'session-b',success:false});localPrinterState={queueName:'Account B printer',ready:false}");
 pending.resolve(usbSession());
 await choosing;

 assert.equal(fixture.run("printerHealth.has('b-printer')"), true);
 assert.equal(fixture.run('localPrinterState.queueName'), 'Account B printer');
 assert.equal(fixture.context.renders, 0);
 assert.equal(fixture.messages.length, 0);
});

test('shared registration transfers cloud credentials directly to the local service without manual code entry', async () => {
 const fixture = application();
 fixture.context.button = fixture.button;

 await fixture.run('enableSharedPrinting(button)');

 assert.deepEqual(fixture.calls.map(call => call.path), ['/api/bridge/pairing-code', '/api/bridge/pair']);
 const registration = fixture.localCalls.find(call => call.path === '/v1/cloud');
 assert.ok(registration);
 assert.deepEqual(JSON.parse(registration.request.body), {
  server: 'https://kivra.example', token: 'cloud-bridge-token', deviceName: 'Registered kitchen PC'
 });
 assert.equal(fixture.context.localStorage.kivraPrintMode, undefined, 'sharing must retain direct printing on the USB host');
});
