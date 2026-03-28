var currentPage = 'home';
var pages = ['home', 'about', 'contact'];

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

var count = 0;
function tick() {
    count++;
    var el = document.getElementById('counter');
    if (el) el.textContent = String(count);
}
setInterval(tick, 1000);

window.mio.invoke('getHello')
    .then(function (data) {
        var el = document.getElementById('api-result');
        if (el) el.textContent = data.message + ' (' + data.timestamp + ')';
    })
    .catch(function (e) { console.error('mio.invoke error', e); });
