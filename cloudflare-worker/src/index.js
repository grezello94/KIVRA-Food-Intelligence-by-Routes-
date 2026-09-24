export default {
  async fetch(request, env) {
    const origin = new URL(env.KIVRA_ORIGIN);
    const incoming = new URL(request.url);
    const target = new URL(incoming.pathname + incoming.search, origin);

    return fetch(new Request(target, request));
  },
};
