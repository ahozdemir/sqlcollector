// Saves text produced by the server as a file in the browser (job packages).
window.sdcDownload = function (fileName, text, contentType) {
    var blob = new Blob([text], { type: contentType || 'application/octet-stream' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
};
