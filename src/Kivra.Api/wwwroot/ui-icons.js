/* KIVRA interface icons: one grid, one stroke weight, inherited color. */
(function () {
  const paths = {
    home: '<path d="m3 10 9-7 9 7"/><path d="M5 9v12h14V9M9 21v-7h6v7"/>',
    timer: '<circle cx="12" cy="13" r="8"/><path d="M12 9v4l3 2M9 2h6M12 2v3M18 5l2-2"/>',
    history: '<path d="M3 11a9 9 0 1 1 2.7 7.4M3 4v7h7"/><path d="M12 7v5l3 2"/>',
    box: '<path d="m12 3 8 4.5v9L12 21l-8-4.5v-9L12 3Z"/><path d="m4 7.5 8 4.5 8-4.5M12 12v9M8 5.2l8 4.5"/>',
    settings: '<path d="m9.3 3-.5 2.4-2 .9-2.1-.8-2.7 4.6 1.8 1.6v2.6L2 15.9l2.7 4.6 2.1-.8 2 .9.5 2.4h5.4l.5-2.4 2-.9 2.1.8 2.7-4.6-1.8-1.6v-2.6l1.8-1.6-2.7-4.6-2.1.8-2-.9L14.7 3H9.3Z"/><circle cx="12" cy="13" r="3"/>',
    printer: '<path d="M7 8V3h10v5M7 17H4V9h16v8h-3M7 14h10v7H7v-7Z"/><path d="M17 11h.01M10 17h4"/>',
    'chevron-left': '<path d="m15 5-7 7 7 7"/>',
    'chevron-right': '<path d="m9 5 7 7-7 7"/>',
    'arrow-right': '<path d="M4 12h16m-6-6 6 6-6 6"/>',
    bell: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4M12 2V1"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
    sparkles: '<path d="m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5L12 3Z"/><path d="M20 2v4M18 4h4"/>',
    grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    minus: '<path d="M5 12h14"/>',
    label: '<path d="M13 3H4v9l9 9 8-8-8-10Z"/><circle cx="8" cy="7" r="1"/>',
    eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    activity: '<path d="M3 12h4l3-8 4 16 3-8h4"/>',
    snowflake: '<path d="M12 2v20M3.3 7l17.4 10M3.3 17 20.7 7M9 4l3 3 3-3M9 20l3-3 3 3M3.5 10.5l4-1-1-4M20.5 13.5l-4 1 1 4M3.5 13.5l4 1-1 4M20.5 10.5l-4-1 1-4"/>',
    users: '<circle cx="9" cy="8" r="3"/><path d="M3 21v-3a6 6 0 0 1 12 0v3M16 5a3 3 0 0 1 0 6M18 15a5 5 0 0 1 3 4v2"/>',
    network: '<rect x="8" y="3" width="8" height="5" rx="1"/><path d="M12 8v5M5 13h14M5 13v3M19 13v3"/><rect x="2" y="16" width="6" height="5" rx="1"/><rect x="16" y="16" width="6" height="5" rx="1"/>',
    download: '<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7h.01"/>',
    refresh: '<path d="M20 7a9 9 0 0 0-15-1L3 9M3 4v5h5M4 17a9 9 0 0 0 15 1l2-3M21 20v-5h-5"/>',
    usb: '<path d="M12 22V3m-3 3 3-3 3 3M12 17l-5-4V9M12 13l5-4V6"/><circle cx="7" cy="7" r="2"/><rect x="15" y="2" width="4" height="4" rx=".5"/><circle cx="12" cy="20" r="2"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="8" cy="8" r="1.5"/><path d="m3 17 5-5 4 4 4-6 5 7"/>',
    leaf: '<path d="M20 3c0 11-4 17-11 17a6 6 0 0 1-6-6C3 7 9 3 20 3Z"/><path d="M3 21 15 9M9 15v-4M9 15h4"/>',
    bowl: '<path d="M3 11h18a9 9 0 0 1-18 0ZM8 20h8M7 7V3M12 7V2M17 7V3"/>',
    pot: '<path d="M5 10h14v9a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-9ZM3 10h18M2 14h3M19 14h3M8 6h8M12 6V4"/>',
    jar: '<path d="M8 3h8v4H8V3ZM7 7h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2Z"/><path d="M5 12h14M5 17h14"/>',
    wheat: '<path d="M12 3v19M12 8C8 8 5 5 5 2c4 0 7 3 7 6ZM12 13c-4 0-7-3-7-6 4 0 7 3 7 6ZM12 18c-4 0-7-3-7-6 4 0 7 3 7 6ZM12 8c4 0 7-3 7-6-4 0-7 3-7 6ZM12 13c4 0 7-3 7-6-4 0-7 3-7 6ZM12 18c4 0 7-3 7-6-4 0-7 3-7 6Z"/>',
    meat: '<path d="M13 3c5-1 9 3 8 8-1 4-5 6-9 5l-4 4-3-3 4-4C8 9 9 4 13 3Z"/><path d="M5 17a2 2 0 1 0-3 3 2 2 0 1 0 3 3l3-3M14 6c2 0 4 2 4 4"/>',
    fish: '<path d="M3 12c4-7 11-7 15 0-4 7-11 7-15 0ZM18 12l4-5v10l-4-5Z"/><path d="M10 7c-2 3-2 7 0 10M6 11h.01"/>',
    milk: '<path d="M9 2h6v5l3 4v11H6V11l3-4V2ZM9 7h6M6 11h12M10 15h4v4h-4v-4Z"/>',
    pepper: '<path d="M14 7c-2-3-6-3-8 0-2 3 0 8-3 14 8 0 17-7 16-12-.5-2-3-3-5-2ZM14 7c0-3 2-5 5-5M7 7c2 2 5 2 7 0"/>',
    layers: '<path d="m12 3 10 5-10 5L2 8l10-5ZM2 12l10 5 10-5M2 16l10 5 10-5"/>',
    'log-out': '<path d="M10 3H4v18h6M10 12h11m-5-5 5 5-5 5"/>',
    save: '<path d="M4 3h13l4 4v14H3V3h1ZM7 3v6h9V3M7 21v-7h10v7"/>',
    shield: '<path d="m12 2 9 4v6c0 5-9 10-9 10S3 17 3 12V6l9-4Z"/><path d="m8 12 3 3 5-6"/>',
    server: '<rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M7 6.5h.01M7 17.5h.01M11 6.5h6M11 17.5h6"/>',
    close: '<path d="m6 6 12 12M18 6 6 18"/>'
  };
  globalThis.icon = function (name, size = 24, className = '') {
    const path = paths[name] || paths.grid;
    const dimension = Number.isFinite(Number(size)) ? Math.max(12, Math.min(96, Number(size))) : 24;
    const classes = ['ui-icon', className].filter(Boolean).join(' ').replace(/[^a-zA-Z0-9_ -]/g, '');
    return `<svg class="${classes}" width="${dimension}" height="${dimension}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">${path}</svg>`;
  };
}());
