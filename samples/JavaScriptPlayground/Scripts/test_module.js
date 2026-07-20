console.log("=== INSIDE MODULE ===");
console.log("globalThis: " + globalThis);
console.log("globalThis.document: " + globalThis.document);
console.log("globalThis.document.isProxy: " + (globalThis.document ? globalThis.document.isProxy : undefined));
console.log("window.document: " + window.document);
console.log("window.document.isProxy: " + (window.document ? window.document.isProxy : undefined));
console.log("document: " + document);
console.log("document.isProxy: " + (document ? document.isProxy : undefined));
