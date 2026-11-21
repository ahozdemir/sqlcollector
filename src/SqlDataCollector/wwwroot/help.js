// Help page language: remembered per browser; first visit follows the browser language (Turkish -> "tr", otherwise "en").
window.sdcHelp = {
    getLanguage: function () {
        try {
            var saved = localStorage.getItem('sdc-help-lang');
            if (saved === 'tr' || saved === 'en') return saved;
        } catch (e) { /* storage blocked */ }
        var nav = (navigator.languages && navigator.languages[0]) || navigator.language || '';
        return nav.toLowerCase().indexOf('tr') === 0 ? 'tr' : 'en';
    },
    setLanguage: function (lang) {
        try { localStorage.setItem('sdc-help-lang', lang); } catch (e) { /* storage blocked */ }
    },
    // the topic in the address (#topic) exists in both languages: scroll to it again after switching
    scrollToHash: function () {
        if (!location.hash) return;
        var el = document.getElementById(decodeURIComponent(location.hash.substring(1)));
        if (el) el.scrollIntoView();
    }
};
