const {test: nodeTest} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {resolve} = require('node:path');
const vm = require('node:vm');
const test = (name, run) => nodeTest(name, {timeout: 2000}, run);

const source = readFileSync(resolve(__dirname, '../../src/Kivra.Api/wwwroot/app.js'), 'utf8');
const requestSource = source.slice(source.indexOf('async function networkRequest('), source.indexOf('async function request('));
const deferred = () => {
 let resolve, reject;
 const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
 return {promise, resolve, reject};
};
const response = (status, body) => ({status, ok: status >= 200 && status < 300, text: async () => JSON.stringify(body)});

function fakeTimers() {
 let now = 0, nextId = 0;
 const pending = new Map(), delays = [];
 return {
  pending, delays,
  setTimeout(callback, delay) { const id = ++nextId; delays.push(delay); pending.set(id, {callback, at: now + delay}); return id; },
  clearTimeout(id) { pending.delete(id); },
  advance(milliseconds) {
   const until = now + milliseconds;
   while (true) {
    const next = [...pending].filter(([, timer]) => timer.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
    if (!next) break;
    now = next[1].at;
    pending.delete(next[0]);
    next[1].callback();
   }
   now = until;
  }
 };
}

function trackedCaller() {
 const controller = new AbortController(), {signal} = controller;
 const add = signal.addEventListener.bind(signal), remove = signal.removeEventListener.bind(signal), listeners = new Map();
 signal.addEventListener = (type, listener, options) => {
  if (type !== 'abort') return add(type, listener, options);
  const wrapped = event => {
   if (options?.once) listeners.delete(listener);
   if (typeof listener === 'function') listener.call(signal, event);
   else listener.handleEvent(event);
  };
  listeners.set(listener, wrapped);
  add(type, wrapped, options);
 };
 signal.removeEventListener = (type, listener, options) => {
  const wrapped = type === 'abort' ? listeners.get(listener) : null;
  if (wrapped) listeners.delete(listener);
  remove(type, wrapped || listener, options);
 };
 return {controller, signal, listeners};
}

function application(fetchResult) {
 const timers = fakeTimers(), calls = [], result = fetchResult || deferred();
 const context = vm.createContext({
  state: {token: ''}, navigator: {onLine: true}, AbortController, AbortSignal,
  setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout,
  signOut() { context.signOutCalls++; }, signOutCalls: 0,
  fetch(path, options) {
   calls.push({path, options});
   return new Promise((resolve, reject) => {
    const abort = () => reject(options.signal.reason || Object.assign(Error('signal is aborted without reason'), {name: 'AbortError'}));
    if (options.signal.aborted) { abort(); return; }
    options.signal.addEventListener('abort', abort, {once: true});
    result.promise.then(resolve, reject).finally(() => options.signal.removeEventListener('abort', abort));
   });
  }
 });
 vm.runInContext(requestSource, context);
 return {calls, context, result, timers, request: context.networkRequest};
}

function loginApplication() {
 const app = application(), {context} = app;
 let submitHandler;
 const button = {disabled: false, textContent: 'Sign in'};
 const nodes = {
  '#pin': {value: '1234'},
  '#loginError': {hidden: true, textContent: ''},
  '#loginDialog': {opened: true, close() { this.opened = false; }},
  '#loginForm': {
   querySelector: () => button,
   addEventListener(type, listener) { assert.equal(type, 'submit'); submitHandler = listener; }
  }
 };
 context.document = {querySelector: selector => nodes[selector]};
 context.sessionStorage = {};
 context.savedSessions = [];
 context.KivraOffline = {async saveSession(session) { context.savedSessions.push(JSON.parse(JSON.stringify(session))); }};
 context.startCalls = 0;
 context.start = async () => { context.startCalls++; };
 context.showToast = () => {};
 const loginStart = source.indexOf("document.querySelector('#loginForm').addEventListener('submit'");
 vm.runInContext(source.slice(loginStart, source.indexOf('async function start()', loginStart)), context);
 return {...app, nodes, button, submit: () => submitHandler({preventDefault() {}, currentTarget: nodes['#loginForm']})};
}

test('a slow PIN sign-in can finish after the former 15-second deadline', async () => {
 const {request, result, calls, timers} = application();
 const pending = request('/api/auth/pin', {method: 'POST', body: '{"pin":"1234"}'});
 assert.deepEqual(timers.delays, [60000]);
 timers.advance(20000);
 assert.equal(calls[0].options.signal.aborted, false);
 const session = {token: 'signed-in', user: {id: 'chef-1'}};
 result.resolve(response(200, session));
 assert.equal((await pending).token, session.token);
 assert.equal(timers.pending.size, 0);
 timers.advance(60000);
 assert.equal(calls[0].options.signal.aborted, false, 'a completed request must not be aborted later');
 assert.equal(calls.length, 1);
});

test('PIN sign-in timeout provides a useful message and never retries its POST', async () => {
 const {request, calls, timers} = application();
 const pending = request('/api/auth/pin', {method: 'POST', body: '{"pin":"1234"}'});
 const rejected = assert.rejects(pending, error => {
  assert.match(error.message, /sign.?in/i);
  assert.match(error.message, /timed out|taking longer|try again/i);
  assert.doesNotMatch(error.message, /signal is aborted|aborterror/i);
  assert.equal(error.network, true);
  assert.equal(error.status, undefined);
  return true;
 });
 timers.advance(59999);
 assert.equal(calls[0].options.signal.aborted, false);
 timers.advance(1);
 await rejected;
 timers.advance(120000);
 assert.equal(calls.length, 1, 'an uncertain sign-in request must not issue another POST automatically');
 assert.equal(timers.pending.size, 0);
});

test('ordinary reads and mutations retain their shorter request budgets', async () => {
 for (const [options, expectedDelay] of [[{}, 8000], [{method: 'GET'}, 8000], [{method: 'HEAD'}, 8000], [{method: 'POST'}, 15000], [{method: 'PUT'}, 15000], [{method: 'DELETE'}, 15000]]) {
  const {request, result, timers} = application();
  const pending = request('/api/items', options);
  assert.deepEqual(timers.delays, [expectedDelay]);
  result.resolve(response(200, []));
  await pending;
  assert.equal(timers.pending.size, 0);
 }
});

test('an incorrect PIN remains a 401 response rather than a network timeout', async () => {
 const {request, result, timers, calls, context} = application();
 const pending = request('/api/auth/pin', {method: 'POST', body: '{"pin":"0000"}'});
 const rejected = assert.rejects(pending, error => {
  assert.equal(error.status, 401);
  assert.equal(error.network, undefined);
  assert.match(error.message, /incorrect pin/i);
  return true;
 });
 result.resolve(response(401, {error: 'Unauthorized'}));
 await rejected;
 assert.equal(context.signOutCalls, 0);
 assert.equal(calls.length, 1);
 assert.equal(timers.pending.size, 0);
});

test('a supplied caller signal cannot disable the internal sign-in timeout', async () => {
 const {request, timers, calls} = application(), caller = trackedCaller();
 const pending = request('/api/auth/pin', {method: 'POST', signal: caller.signal});
 const rejected = assert.rejects(pending, error => {
  assert.match(error.message, /sign.?in/i);
  assert.equal(error.network, true);
  return true;
 });
 timers.advance(60000);
 await rejected;
 assert.equal(calls[0].options.signal.aborted, true);
 assert.equal(caller.signal.aborted, false, 'timing out a request must not abort its caller');
 assert.equal(caller.listeners.size, 0);
 assert.equal(timers.pending.size, 0);
});

test('caller cancellation reaches fetch immediately and releases request resources', async () => {
 const {request, timers, calls} = application(), caller = trackedCaller();
 const cancellation = Object.assign(Error('User cancelled this request.'), {name: 'AbortError'});
 const pending = request('/api/auth/pin', {method: 'POST', signal: caller.signal});
 const rejected = assert.rejects(pending, error => {
  assert.equal(error.message, cancellation.message);
  assert.doesNotMatch(error.message, /timed out/i);
  return true;
 });
 caller.controller.abort(cancellation);
 await rejected;
 assert.equal(calls[0].options.signal.aborted, true);
 assert.equal(caller.listeners.size, 0);
 assert.equal(timers.pending.size, 0);
});

test('a previously cancelled caller does not leave a live deadline behind', async () => {
 const {request, timers} = application(), caller = trackedCaller();
 caller.controller.abort(Object.assign(Error('Navigation cancelled the request.'), {name: 'AbortError'}));
 await assert.rejects(request('/api/items', {signal: caller.signal}), /Navigation cancelled the request/);
 assert.equal(caller.listeners.size, 0);
 assert.equal(timers.pending.size, 0);
});

test('successful requests remove a caller cancellation listener and deadline', async () => {
 const {request, result, timers, calls} = application(), caller = trackedCaller();
 const pending = request('/api/auth/pin', {method: 'POST', signal: caller.signal});
 result.resolve(response(200, {token: 'signed-in'}));
 await pending;
 assert.equal(caller.listeners.size, 0);
 assert.equal(timers.pending.size, 0);
 caller.controller.abort(Error('A later request was cancelled.'));
 assert.equal(calls[0].options.signal.aborted, false, 'a completed request must no longer follow caller cancellation');
});

test('network failures also release the caller listener and deadline', async () => {
 const {request, result, timers} = application(), caller = trackedCaller();
 const pending = request('/api/auth/pin', {method: 'POST', signal: caller.signal});
 const rejected = assert.rejects(pending, error => {
  assert.equal(error.network, true);
  return true;
 });
 result.reject(TypeError('Failed to fetch'));
 await rejected;
 assert.equal(caller.listeners.size, 0);
 assert.equal(timers.pending.size, 0);
});

test('a failed sign-in connection explains how to recover without exposing fetch errors', async () => {
 const {request, result, timers, calls} = application();
 const pending = request('/api/auth/pin', {method: 'POST'});
 const rejected = assert.rejects(pending, error => {
  assert.match(error.message, /sign.?in/i);
  assert.match(error.message, /connection|internet|reconnect/i);
  assert.doesNotMatch(error.message, /Failed to fetch|signal is aborted/i);
  assert.equal(error.network, true);
  assert.equal(error.status, undefined);
  return true;
 });
 result.reject(TypeError('Failed to fetch'));
 await rejected;
 assert.equal(calls.length, 1);
 assert.equal(timers.pending.size, 0);
});

test('offline sign-in does not send a request and gives a connection message', async () => {
 const {request, context, timers, calls} = application(), caller = trackedCaller();
 context.navigator.onLine = false;
 await assert.rejects(request('/api/auth/pin', {method: 'POST', signal: caller.signal}), error => {
  assert.match(error.message, /internet|connection|reconnect/i);
  assert.equal(error.network, true);
  assert.equal(error.status, undefined);
  return true;
 });
 assert.equal(calls.length, 0);
 assert.equal(caller.listeners.size, 0);
 assert.equal(timers.pending.size, 0);
});

test('sign-in throttling and server failures preserve their HTTP status without automatic POST retries', async () => {
 for (const status of [429, 500, 503]) {
  const {request, result, timers, calls} = application(), caller = trackedCaller();
  const pending = request('/api/auth/pin', {method: 'POST', signal: caller.signal});
  const rejected = assert.rejects(pending, error => {
   assert.equal(error.status, status);
   assert.equal(error.network, undefined, 'an HTTP error must remain distinct from a lost connection');
   assert.equal(error.timeout, undefined);
   assert.match(error.message, status === 429 ? /too many|wait|attempts/i : /server|unavailable|try again/i);
   assert.doesNotMatch(error.message, /incorrect pin|signal is aborted/i);
   return true;
  });
  result.resolve(response(status, {title: 'Internal server details should not appear on sign-in.'}));
  await rejected;
  timers.advance(120000);
  assert.equal(calls.length, 1);
  assert.equal(caller.listeners.size, 0);
  assert.equal(timers.pending.size, 0);
 }
});

test('the login form keeps one pending sign-in across the former deadline and stores a successful session', async () => {
 const {submit, nodes, button, result, timers, calls, context} = loginApplication();
 const pending = submit();
 assert.equal(button.disabled, true);
 assert.match(button.textContent, /signing in/i);
 assert.equal(nodes['#loginError'].hidden, true);
 timers.advance(20000);
 assert.equal(button.disabled, true);
 assert.match(button.textContent, /signing in/i);
 assert.equal(nodes['#loginDialog'].opened, true);
 await submit();
 assert.equal(calls.length, 1, 'submitting while sign-in is pending must not create another POST');
 assert.equal(JSON.parse(calls[0].options.body).pin, '1234');
 const session = {token: 'signed-in', user: {id: 'chef-1', displayName: 'Kitchen chef'}, expiresAt: '2027-01-01T00:00:00Z'};
 result.resolve(response(200, session));
 await pending;
 assert.equal(context.state.token, session.token);
 assert.equal(context.state.user.id, session.user.id);
 assert.equal(context.state.expiresAt, session.expiresAt);
 assert.equal(context.sessionStorage.kivraToken, session.token);
 assert.equal(context.sessionStorage.kivraUser, JSON.stringify(session.user));
 assert.equal(context.sessionStorage.kivraExpiresAt, session.expiresAt);
 assert.deepEqual(context.savedSessions, [session]);
 assert.equal(nodes['#pin'].value, '');
 assert.equal(nodes['#loginDialog'].opened, false);
 assert.equal(nodes['#loginError'].hidden, true);
 assert.equal(context.startCalls, 1);
 assert.equal(button.disabled, false);
 assert.equal(button.textContent, 'Sign in');
 assert.equal(timers.pending.size, 0);
 assert.equal(calls.length, 1);
});

test('a login form timeout keeps the PIN, restores the button, and allows an explicit retry', async () => {
 const {submit, nodes, button, result, timers, calls, context} = loginApplication();
 const pending = submit();
 timers.advance(60000);
 await pending;
 assert.equal(nodes['#loginError'].hidden, false);
 assert.match(nodes['#loginError'].textContent, /sign.?in/i);
 assert.match(nodes['#loginError'].textContent, /timed out|taking longer|try again/i);
 assert.doesNotMatch(nodes['#loginError'].textContent, /signal is aborted|aborterror/i);
 assert.equal(nodes['#pin'].value, '1234');
 assert.equal(button.disabled, false);
 assert.equal(button.textContent, 'Sign in');
 assert.equal(nodes['#loginDialog'].opened, true);
 assert.equal(context.state.token, '');
 assert.equal(context.startCalls, 0);
 assert.equal(context.savedSessions.length, 0);
 assert.equal(calls.length, 1);
 assert.equal(timers.pending.size, 0);
 const retry = submit();
 assert.equal(nodes['#loginError'].hidden, true);
 assert.equal(calls.length, 2);
 assert.equal(JSON.parse(calls[1].options.body).pin, '1234');
 result.resolve(response(200, {token: 'retry-session', user: {id: 'chef-1'}, expiresAt: '2027-01-01T00:00:00Z'}));
 await retry;
 assert.equal(context.state.token, 'retry-session');
 assert.equal(nodes['#loginDialog'].opened, false);
 assert.equal(context.startCalls, 1);
 assert.equal(timers.pending.size, 0);
});

test('the login form keeps incorrect PIN errors distinct and remains ready for correction', async () => {
 const {submit, nodes, button, result, calls, context} = loginApplication();
 const pending = submit();
 result.resolve(response(401, {error: 'Unauthorized'}));
 await pending;
 assert.equal(nodes['#loginError'].hidden, false);
 assert.match(nodes['#loginError'].textContent, /incorrect pin/i);
 assert.doesNotMatch(nodes['#loginError'].textContent, /timed out|signal is aborted/i);
 assert.equal(nodes['#pin'].value, '1234');
 assert.equal(button.disabled, false);
 assert.equal(button.textContent, 'Sign in');
 assert.equal(nodes['#loginDialog'].opened, true);
 assert.equal(context.state.token, '');
 assert.equal(context.startCalls, 0);
 assert.equal(context.savedSessions.length, 0);
 assert.equal(calls.length, 1);
});
