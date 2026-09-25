// Turns a string into a downloaded file, entirely in the browser: the export never
// travels through a URL that could be shared, logged or leaked.
window.sangamDownload = function (fileName, contents) {
    const blob = new Blob([contents], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};
