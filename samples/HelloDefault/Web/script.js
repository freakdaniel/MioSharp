// ─── SPA Router ──────────────────────────────────────────────────────────────
var currentPage = 'home';
var pages = ['home', 'invoke', 'about'];

function navigate(page) {
    for (var i = 0; i < pages.length; i++) {
        var pageEl = document.getElementById('page-' + pages[i]);
        if (pageEl) pageEl.style.display = 'none';
        var navEl = document.getElementById('nav-' + pages[i]);
        if (navEl) navEl.className = 'nav-item';
    }
    var target = document.getElementById('page-' + page);
    if (target) target.style.display = '';
    var activeNav = document.getElementById('nav-' + page);
    if (activeNav) activeNav.className = 'nav-item active';
    currentPage = page;
}

// ─── Live counter ─────────────────────────────────────────────────────────────
var count = 0;
setInterval(function() {
    count++;
    var el = document.getElementById('counter');
    if (el) el.textContent = String(count);
}, 1000);

// ─── mio.invoke: load platform info on startup ───────────────────────────────
window.mio.invoke('getPlatformInfo')
    .then(function(info) {
        var el = document.getElementById('platform-info');
        if (el) el.textContent = info.os + ' · ' + info.runtime + ' · ' + info.arch;
    })
    .catch(function(e) { console.error('getPlatformInfo error:', e); });

// ─── mio.invoke: fetch time from C# ──────────────────────────────────────────
function fetchTime() {
    window.mio.invoke('getTime')
        .then(function(data) {
            var el = document.getElementById('time-result');
            if (el) el.textContent = data.date + ' ' + data.time;
        })
        .catch(function(e) { console.error('getTime error:', e); });
}

// ─── mio.invoke: greet from C# ───────────────────────────────────────────────
function greetUser() {
    window.mio.invoke('greet', { name: 'MioSharp' })
        .then(function(data) {
            var el = document.getElementById('greet-result');
            if (el) el.textContent = data.message;
        })
        .catch(function(e) { console.error('greet error:', e); });
}

// Load time on startup
fetchTime();
