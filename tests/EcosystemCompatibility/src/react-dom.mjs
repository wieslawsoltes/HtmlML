import React, { startTransition, Suspense, useState } from "react";
import { createPortal, flushSync } from "react-dom";
import { createRoot, hydrateRoot } from "react-dom/client";
import { assert, delay, equal, run } from "./harness.mjs";

const fixture = document.getElementById("fixture-root");
const portalHost = document.getElementById("portal-root");
let setItems;
let setCount;
let setValue;

async function eventually(predicate, message, timeoutMilliseconds = 2500) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await delay(25);
  }
  throw new Error(message);
}

function App() {
  const [items, updateItems] = useState(["a", "b", "c"]);
  const [count, updateCount] = useState(0);
  const [value, updateValue] = useState("initial");
  setItems = updateItems;
  setCount = updateCount;
  setValue = updateValue;
  return React.createElement(React.Fragment, null,
    React.createElement("button", { id: "counter", onClick: () => updateCount(current => current + 1) }, String(count)),
    React.createElement("input", { id: "controlled", value, onInput: event => updateValue(event.currentTarget.value) }),
    React.createElement("ul", { id: "items" }, items.map(item => React.createElement("li", { key: item, "data-key": item }, item))),
    createPortal(React.createElement("span", { id: "portal-child" }, `portal:${count}`), portalHost));
}

const root = createRoot(fixture);
flushSync(() => root.render(React.createElement(App)));

run([
  ["createRoot mounts host nodes and a portal", () => {
    equal(document.querySelectorAll("#items > li").length, 3, "mounted list length");
    equal(document.getElementById("portal-child")?.textContent, "portal:0", "portal text");
    assert(document.getElementById("portal-child").parentNode === portalHost, "portal parent identity");
  }],
  ["synthetic click batches a state update and portal reconciliation", () => {
    flushSync(() => document.getElementById("counter").click());
    equal(document.getElementById("counter").textContent, "1", "counter state");
    equal(document.getElementById("portal-child").textContent, "portal:1", "portal reconciliation");
  }],
  ["keyed reorder preserves DOM node identity", () => {
    const original = document.querySelector("[data-key='a']");
    flushSync(() => setItems(["c", "b", "a"]));
    equal(Array.from(document.querySelectorAll("#items > li")).map(node => node.textContent).join(","), "c,b,a", "keyed order");
    assert(document.querySelector("[data-key='a']") === original, "keyed node identity was not preserved");
  }],
  ["controlled input reconciles programmatic state", () => {
    flushSync(() => setValue("updated"));
    equal(document.getElementById("controlled").value, "updated", "controlled value");
  }],
  ["batched state setters commit their final value", () => {
    flushSync(() => {
      setCount(value => value + 1);
      setCount(value => value + 1);
    });
    equal(document.getElementById("counter").textContent, "3", "batched count");
  }],
  ["unmount removes root and portal subtrees", () => {
    flushSync(() => root.unmount());
    equal(fixture.childNodes.length, 0, "root child count after unmount");
    equal(portalHost.childNodes.length, 0, "portal child count after unmount");
  }],
  ["hydrateRoot reuses matching server markup and attaches events", async () => {
    fixture.innerHTML = `<button id="hydrated">server</button>`;
    const serverButton = fixture.firstChild;
    let activations = 0;
    const recoverableErrors = [];
    const hydratedRoot = hydrateRoot(fixture,
      React.createElement("button", { id: "hydrated", onClick: () => activations++ }, "server"),
      { onRecoverableError: error => recoverableErrors.push(
        `${String(error?.message || error)}${error?.cause ? ` cause=${String(error.cause?.stack || error.cause)}` : ""}`) });
    await delay(30);
    assert(fixture.firstChild === serverButton,
      `hydration replaced matching host markup: ${recoverableErrors.join(" | ") || "no recoverable error"}`);
    flushSync(() => fixture.firstChild.click());
    equal(activations, 1, "hydrated synthetic click");
    flushSync(() => hydratedRoot.unmount());
    equal(fixture.childNodes.length, 0, "hydrated root cleanup");
  }],
  ["startTransition schedules and commits a non-urgent host update", async () => {
    let setLabel;
    function TransitionApp() {
      const [label, updateLabel] = useState("idle");
      setLabel = updateLabel;
      return React.createElement("span", { id: "transition-value" }, label);
    }
    const transitionRoot = createRoot(fixture);
    flushSync(() => transitionRoot.render(React.createElement(TransitionApp)));
    startTransition(() => setLabel("ready"));
    await eventually(() => document.getElementById("transition-value")?.textContent === "ready",
      "transition update did not commit");
    flushSync(() => transitionRoot.unmount());
    equal(fixture.childNodes.length, 0, "transition root cleanup");
  }],
  ["Suspense replaces fallback after a lazy module resolves", async () => {
    let resolveModule;
    const LazyValue = React.lazy(() => new Promise(resolve => { resolveModule = resolve; }));
    const suspenseRoot = createRoot(fixture);
    flushSync(() => suspenseRoot.render(
      React.createElement(Suspense, { fallback: React.createElement("span", { id: "suspense-fallback" }, "loading") },
        React.createElement(LazyValue))));
    equal(document.getElementById("suspense-fallback")?.textContent, "loading", "Suspense fallback text");
    resolveModule({
      default: () => React.createElement("span", { id: "suspense-value" }, "ready")
    });
    await eventually(() => document.getElementById("suspense-value")?.textContent === "ready",
      "resolved lazy child did not replace the Suspense fallback");
    equal(document.getElementById("suspense-fallback"), null, "Suspense fallback survived resolution");
    flushSync(() => suspenseRoot.unmount());
    equal(fixture.childNodes.length, 0, "Suspense root cleanup");
  }]
]);
