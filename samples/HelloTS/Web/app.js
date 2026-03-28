// Pre-compiled from app.ts (TypeScript → ES6)
// To recompile: npm install -g typescript && tsc (in Web/src/)
"use strict";
var pages = ['home', 'compute'];
var currentPage = 'home';
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
window.navigate = navigate;

function updateClock() {
    window.mio.invoke('getServerTime')
        .then(function (data) {
            var el = document.getElementById('clock');
            if (el) el.textContent = data.local;
        })
        .catch(function (e) { console.error('clock error:', e); });
}
updateClock();
setInterval(updateClock, 1000);

function runCompute() {
    var aEl = document.getElementById('input-a');
    var bEl = document.getElementById('input-b');
    if (!aEl || !bEl) return;
    var a = parseFloat(aEl.value) || 0;
    var b = parseFloat(bEl.value) || 0;
    window.mio.invoke('compute', { a: a, b: b })
        .then(function (result) {
            var el = document.getElementById('compute-result');
            if (el) {
                el.textContent =
                    a + ' + ' + b + ' = ' + result.sum + '\n' +
                    a + ' x ' + b + ' = ' + result.product + '\n' +
                    a + ' ^ ' + b + ' = ' + result.power;
            }
        })
        .catch(function (e) { console.error('compute error:', e); });
}
window.runCompute = runCompute;
