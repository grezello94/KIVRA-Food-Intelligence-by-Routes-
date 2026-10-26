const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {resolve} = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../src/Kivra.Api/wwwroot/app.js'), 'utf8');
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return {promise, resolve}; };

function application() {
 const calls = [], messages = [], handlers = [];
 const context = vm.createContext({
  sessionStorage: {kivraToken: 'session-a', kivraUser: JSON.stringify({id: 'admin-a', role: 'Administrator'})},
  localStorage: {},
  document: {querySelector: () => ({addEventListener(name, callback) { handlers.push({name, callback}); }}), documentElement: {classList: {add() {}}}},
  MutationObserver: class {observe() {}},
  icon: () => '', page: html => html,
  render() { context.renders++; }, renders: 0,
  showToast(message, kind) { messages.push({message, kind}); },
  setTimeout, clearTimeout, setInterval(callback) { context.tick = callback; }, AbortController, Uint8Array,
  fetch: async () => { throw Error('Unexpected network request'); }
 });
 vm.runInContext(source.slice(0, source.indexOf('function brandLogo')), context);
 for (const [start, end] of [
  ['function printerCard(', 'function categoryExpiryCount('],
  ['function moreView(', 'function printersView('],
  ['async function testPrint()', 'async function checkStorage(']
 ]) vm.runInContext(source.slice(source.indexOf(start), source.indexOf(end, source.indexOf(start))), context);
 const run = code => vm.runInContext(code, context);
 run("state.printers=[{id:'printer-a',name:'Kitchen Printer',model:'TSC TE210',driver:'TscTsplUsb'}];state.view='more'");
 context.reply = {success: true};
 context.network = async (path, options) => { calls.push({path, options}); return context.reply; };
 run('networkRequest=network');
 const retryStart = source.indexOf('app.addEventListener(\'click\',async e=>{const button=e.target.closest(\'[data-action="refresh-printer"]\')');
 assert.notEqual(retryStart, -1);
 vm.runInContext(source.slice(retryStart, source.indexOf('\n', retryStart)), context);
 vm.runInContext(source.slice(source.indexOf('setInterval(()=>{'), source.indexOf('// Let the first screen')), context);
 return {context, calls, messages, handlers, run};
}

test('a configured printer is not presented as ready before its connection is checked', () => {
 const {run} = application();
 assert.equal(run('printerReadiness().label'), 'Configured');
 assert.doesNotMatch(run('moreView()'), />Ready</);
 assert.match(run('printerCard()'), /status-orb off/);
});

test('readiness makes one live connection check and shares an in-flight check', async () => {
 const {context, calls, run} = application(), pending = deferred();
 context.reply = pending.promise;
 const first = run('refreshPrinterConnection()');
 const second = run('refreshPrinterConnection({force:true})');
 assert.equal(run('printerReadiness().label'), 'Checking…');
 assert.equal(calls.length, 1);
 assert.equal(calls[0].path, '/api/printers/printer-a/test-connection');
 assert.equal(calls[0].options.method, 'POST');
 pending.resolve({success: true});
 await Promise.all([first, second]);
 assert.equal(run('printerReadiness().ready'), true);
 assert.match(run('moreView()'), />Ready</);
});

test('an offline bridge has persistent recovery instructions and a pairing link', async () => {
 const {context, run} = application();
 context.reply = {success: false, error: 'No Windows Print Bridge is online. Pair it again in Settings > Print Bridge.'};
 await run('refreshPrinterConnection()');
 const html = run('moreView()');
 assert.match(html, />Bridge offline</);
 assert.match(html, /Connect printer/);
 assert.match(html, /data-view="bridge"/);
 assert.match(html, /Check again/);
 assert.doesNotMatch(html, />Ready</);
});

test('an unpaired bridge requests pairing instead of showing ready', async () => {
 const {context, run} = application();
 context.reply = {success: false, error: 'No Windows Print Bridge is paired. Generate a code.'};
 await run('refreshPrinterConnection()');
 assert.equal(run('printerReadiness().label'), 'Pairing needed');
});

test('a failed test print replaces a previous ready status with its recovery error', async () => {
 const {context, messages, run} = application();
 await run('refreshPrinterConnection()');
 context.result = {success: false, error: 'No Windows Print Bridge is online.'};
 run('request=async()=>result');
 await run('testPrint()');
 assert.equal(run('printerReadiness().ready'), false);
 assert.match(run('moreView()'), /Connect printer/);
 assert.equal(messages[0].kind, 'error');
});

test('a network error cannot leave a cached green ready indicator', async () => {
 const {context, run} = application();
 await run('refreshPrinterConnection()');
 context.network = async () => { throw Error('The server is offline.'); };
 run('networkRequest=network');
 await run('refreshPrinterConnection({force:true})');
 assert.equal(run('printerReadiness().ready'), false);
 assert.match(run('moreView()'), /The server is offline/);
});

test('an old or different-account connection result cannot show ready', async () => {
 const {run} = application();
 await run('refreshPrinterConnection()');
 run('printerHealth.get("printer-a").checkedAt=Date.now()-61000');
 assert.equal(run('printerReadiness().ready'), false);
 run("printerHealth.get('printer-a').checkedAt=Date.now();state.token='session-b'");
 assert.equal(run('printerReadiness().ready'), false);
});

test('a delayed check from another signed-in account cannot update readiness', async () => {
 const {context, run} = application(), pending = deferred();
 context.reply = pending.promise;
 const check = run('refreshPrinterConnection()');
 run("state.token='session-b'");
 pending.resolve({success: true});
 await check;
 assert.equal(run('printerReadiness().ready'), false);
});

test('readiness errors are escaped and a background check preserves an open printer form', async () => {
 const {context, run} = application();
 context.reply = {success: false, error: '<img src=x onerror=alert(1)>'};
 run("state.view='printer-form'");
 await run('refreshPrinterConnection()');
 assert.equal(context.renders, 0);
 assert.match(run('moreView()'), /&lt;img/);
 assert.doesNotMatch(run('moreView()'), /<img/);
});

test('Check again sends a fresh connection check through the actual click handler', async () => {
 const {context, calls, handlers, run} = application();
 context.reply = {success: false, error: 'No Windows Print Bridge is online.'};
 await run('refreshPrinterConnection()');
 context.reply = {success: true};
 const button = {disabled: false, isConnected: true};
 const handler = handlers.filter(x => x.name === 'click').at(-1).callback;
 await handler({target: {closest: () => button}});
 assert.equal(calls.length, 2);
 assert.equal(run('printerReadiness().ready'), true);
 assert.equal(button.disabled, false);
});

test('a delayed connection check cannot overwrite a newer failed test print', async () => {
 const {context, run} = application(), pending = deferred();
 context.reply = pending.promise;
 const check = run('refreshPrinterConnection()');
 run("request=async()=>({success:false,error:'No Windows Print Bridge is online.'})");
 await run('testPrint()');
 pending.resolve({success: true});
 await check;
 assert.equal(run('printerReadiness().ready'), false);
 assert.equal(run('printerReadiness().label'), 'Bridge offline');
});

test('Settings rechecks while left open and detects a disconnected bridge', async () => {
 const {context, calls, run} = application();
 await run('refreshPrinterConnection()');
 context.reply = {success: false, error: 'No Windows Print Bridge is online.'};
 run('printerHealth.get("printer-a").checkedAt=Date.now()-31000');
 context.tick();
 await run('refreshPrinterConnection()');
 assert.equal(calls.length, 2);
 assert.equal(run('printerReadiness().ready'), false);
 run("state.view='printer-form';printerHealth.get('printer-a').checkedAt=Date.now()-31000");
 context.tick();
 assert.equal(calls.length, 2, 'background polling must preserve printer forms');
});
