// Turns a string into a downloaded file, entirely in the browser: the export never
// travels through a URL that could be shared, logged or leaked.
window.sangamDownloadText = function (fileName, contents) {
    download(fileName, contents, "text/plain");
};

window.sangamDownload = function (fileName, contents) {
    download(fileName, contents, "application/json");
};

function download(fileName, contents, type) {
    const blob = new Blob([contents], { type: type });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
}
