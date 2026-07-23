import { Carousel, Collapse, Dropdown, Modal, Offcanvas, Popover, ScrollSpy, Tab, Tooltip } from "bootstrap";
import { assert, delay, equal, run } from "./harness.mjs";

const fixture = document.getElementById("fixture-root");

function nextEvent(element, type, timeoutMilliseconds = 2500) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      element.removeEventListener(type, listener);
      reject(new Error(`${type} was not dispatched within ${timeoutMilliseconds}ms`));
    }, timeoutMilliseconds);
    function listener(event) {
      clearTimeout(timeout);
      element.removeEventListener(type, listener);
      resolve(event);
    }
    element.addEventListener(type, listener);
  });
}

async function eventually(predicate, message, timeoutMilliseconds = 2500) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await delay(25);
  }
  throw new Error(message);
}

run([
  ["tab activation updates classes ARIA and event order", () => {
    fixture.innerHTML = `
      <div role="tablist">
        <button id="tab-a" class="nav-link active" data-bs-toggle="tab" data-bs-target="#pane-a" aria-selected="true">A</button>
        <button id="tab-b" class="nav-link" data-bs-toggle="tab" data-bs-target="#pane-b" aria-selected="false">B</button>
      </div>
      <div id="pane-a" class="tab-pane active show">A</div><div id="pane-b" class="tab-pane">B</div>`;
    const events = [];
    document.getElementById("tab-b").addEventListener("shown.bs.tab", () => events.push("shown"));
    Tab.getOrCreateInstance(document.getElementById("tab-b")).show();
    assert(document.getElementById("tab-b").classList.contains("active"), "new tab did not activate");
    equal(document.getElementById("tab-b").getAttribute("aria-selected"), "true", "new tab ARIA state");
    assert(document.getElementById("pane-b").classList.contains("active"), "new pane did not activate");
    equal(events.join(","), "shown", "tab event delivery");
  }],
  ["collapse show and hide complete their lifecycle", async () => {
    fixture.innerHTML = `<div id="collapse" class="collapse">content</div>`;
    const element = document.getElementById("collapse");
    const observations = [];
    element.addEventListener("shown.bs.collapse", () => observations.push("shown"));
    element.addEventListener("hidden.bs.collapse", () => observations.push("hidden"));
    const collapse = new Collapse(element, { toggle: false });
    const shown = nextEvent(element, "shown.bs.collapse");
    collapse.show();
    await shown;
    assert(element.classList.contains("show"), "collapse did not enter shown state");
    const hidden = nextEvent(element, "hidden.bs.collapse");
    collapse.hide();
    await hidden;
    assert(!element.classList.contains("show"), "collapse did not leave shown state");
    equal(observations.join(","), "shown,hidden", "collapse lifecycle events");
  }],
  ["dropdown composes Popper placement and menu lifecycle", async () => {
    fixture.innerHTML = `<div class="dropdown"><button id="toggle" data-bs-toggle="dropdown" aria-expanded="false">Menu</button><div id="menu" class="dropdown-menu"><button class="dropdown-item">Item</button></div></div>`;
    const toggle = document.getElementById("toggle");
    const menu = document.getElementById("menu");
    let showEvents = 0;
    let shownEvents = 0;
    toggle.addEventListener("show.bs.dropdown", () => showEvents++);
    toggle.addEventListener("shown.bs.dropdown", () => shownEvents++);
    const dropdown = Dropdown.getOrCreateInstance(toggle);
    dropdown.show();
    if (dropdown._popper) await dropdown._popper.update();
    assert(menu.classList.contains("show"),
      `dropdown menu did not show (menu='${menu.className}', toggle='${toggle.className}', ` +
      `disabled=${String(toggle.disabled)}, aria=${toggle.getAttribute("aria-expanded")}, ` +
      `popper=${Boolean(dropdown._popper)}, events=${showEvents}/${shownEvents})`);
    equal(toggle.getAttribute("aria-expanded"), "true", "dropdown expanded state");
    assert(Boolean(menu.getAttribute("data-popper-placement")), "Popper did not publish placement");
    const scrollParents = [
      ...dropdown._popper.state.scrollParents.reference,
      ...dropdown._popper.state.scrollParents.popper
    ];
    const invalidScrollParents = scrollParents.filter(parent =>
      typeof parent?.addEventListener !== "function" || typeof parent?.removeEventListener !== "function");
    assert(invalidScrollParents.length === 0,
      `Popper scroll parents are not EventTargets: ${invalidScrollParents.map(parent =>
        parent === window ? "window" : parent === document.body ? "body" :
        parent === document.documentElement ? "documentElement" : parent?.nodeName || typeof parent).join(",")}`);
    dropdown.hide();
    assert(!menu.classList.contains("show"), "dropdown menu did not hide");
  }],
  ["modal creates and removes its visible state without animation", () => {
    fixture.innerHTML = `<div id="modal" class="modal" tabindex="-1"><div class="modal-dialog"><div class="modal-content"><button data-bs-dismiss="modal">Close</button></div></div></div>`;
    const element = document.getElementById("modal");
    const modal = new Modal(element, { backdrop: false, focus: false, keyboard: true });
    modal.show();
    assert(element.classList.contains("show"), "modal did not show");
    equal(element.getAttribute("aria-modal"), "true", "modal ARIA state");
    modal.hide();
    assert(!element.classList.contains("show"), "modal did not hide");
    equal(element.getAttribute("aria-modal"), null, "modal ARIA state after hide");
  }],
  ["tooltip composes Popper placement content and disposal", async () => {
    fixture.innerHTML = `<button id="tooltip-toggle" title="Helpful detail">Help</button>`;
    const toggle = document.getElementById("tooltip-toggle");
    const tooltip = new Tooltip(toggle, { animation: false, container: fixture, trigger: "manual", placement: "right" });
    tooltip.show();
    if (tooltip._popper) await tooltip._popper.update();
    const tip = tooltip.tip;
    assert(tip?.classList.contains("show"), "tooltip did not show");
    equal(tip.querySelector(".tooltip-inner")?.textContent, "Helpful detail", "tooltip content");
    equal(tip.getAttribute("role"), "tooltip", "tooltip role");
    assert(Boolean(toggle.getAttribute("aria-describedby")), "tooltip owner lacked aria-describedby");
    tooltip.hide();
    tooltip.dispose();
    equal(fixture.querySelector(".tooltip"), null, "tooltip survived disposal");
  }],
  ["popover composes title body placement and disposal", async () => {
    fixture.innerHTML = `<button id="popover-toggle" title="Heading" data-bs-content="Popover body">Details</button>`;
    const toggle = document.getElementById("popover-toggle");
    const popover = new Popover(toggle, { animation: false, container: fixture, trigger: "manual", placement: "bottom" });
    popover.show();
    if (popover._popper) await popover._popper.update();
    const tip = popover.tip;
    assert(tip?.classList.contains("show"), "popover did not show");
    equal(tip.querySelector(".popover-header")?.textContent, "Heading", "popover title");
    equal(tip.querySelector(".popover-body")?.textContent, "Popover body", "popover body");
    popover.hide();
    popover.dispose();
    equal(fixture.querySelector(".popover"), null, "popover survived disposal");
  }],
  ["offcanvas show and hide complete ARIA and event lifecycle", async () => {
    fixture.innerHTML = `<div id="offcanvas" class="offcanvas offcanvas-start" tabindex="-1"><button data-bs-dismiss="offcanvas">Close</button></div>`;
    const element = document.getElementById("offcanvas");
    const observations = [];
    element.addEventListener("shown.bs.offcanvas", () => observations.push("shown"));
    element.addEventListener("hidden.bs.offcanvas", () => observations.push("hidden"));
    const offcanvas = new Offcanvas(element, { backdrop: false, keyboard: true, scroll: true });
    const shown = nextEvent(element, "shown.bs.offcanvas");
    offcanvas.show();
    await shown;
    assert(element.classList.contains("show"), "offcanvas did not show");
    equal(element.getAttribute("aria-modal"), "true", "offcanvas ARIA state");
    equal(element.getAttribute("role"), "dialog", "offcanvas role");
    const hidden = nextEvent(element, "hidden.bs.offcanvas");
    offcanvas.hide();
    await hidden;
    assert(!element.classList.contains("show"), "offcanvas did not hide");
    equal(element.getAttribute("aria-modal"), null, "offcanvas ARIA state after hide");
    equal(observations.join(","), "shown,hidden", "offcanvas lifecycle events");
    offcanvas.dispose();
  }],
  ["scrollspy tracks a bounded scroll root through IntersectionObserver", async () => {
    fixture.innerHTML = `
      <nav id="scrollspy-nav" class="nav">
        <a id="scrollspy-link-a" class="nav-link" href="#scrollspy-section-a">A</a>
        <a id="scrollspy-link-b" class="nav-link" href="#scrollspy-section-b">B</a>
      </nav>
      <div id="scrollspy-root" style="position:relative;width:240px;height:100px;overflow-y:auto">
        <section id="scrollspy-section-a" style="display:block;height:120px">A</section>
        <section id="scrollspy-section-b" style="display:block;height:120px">B</section>
      </div>`;
    const root = document.getElementById("scrollspy-root");
    const linkA = document.getElementById("scrollspy-link-a");
    const linkB = document.getElementById("scrollspy-link-b");
    const activations = [];
    root.addEventListener("activate.bs.scrollspy", event => activations.push(event.relatedTarget?.id));
    const scrollspy = new ScrollSpy(root, {
      target: document.getElementById("scrollspy-nav"),
      rootMargin: "0px",
      threshold: [0.1, 0.5, 1]
    });
    await eventually(() => linkA.classList.contains("active"),
      "ScrollSpy did not activate the first visible section");
    root.scrollTop = 130;
    root.dispatchEvent(new Event("scroll"));
    await eventually(() => linkB.classList.contains("active"),
      "ScrollSpy did not activate the second section after scrolling");
    assert(!linkA.classList.contains("active"), "ScrollSpy retained the previous active link");
    assert(activations.includes("scrollspy-link-b"), "ScrollSpy activation event lacked the second link");
    scrollspy.dispose();
  }],
  ["carousel completes animated class and event lifecycle", async () => {
    fixture.innerHTML = `
      <div id="carousel" class="carousel slide">
        <div class="carousel-indicators">
          <button class="active" data-bs-target="#carousel" data-bs-slide-to="0" aria-current="true"></button>
          <button data-bs-target="#carousel" data-bs-slide-to="1"></button>
        </div>
        <div class="carousel-inner">
          <div id="carousel-a" class="carousel-item active">A</div>
          <div id="carousel-b" class="carousel-item">B</div>
        </div>
      </div>`;
    const element = document.getElementById("carousel");
    const first = document.getElementById("carousel-a");
    const second = document.getElementById("carousel-b");
    const lifecycle = [];
    element.addEventListener("slide.bs.carousel", event => lifecycle.push(`slide:${event.from}-${event.to}`));
    element.addEventListener("slid.bs.carousel", event => lifecycle.push(`slid:${event.from}-${event.to}`));
    const carousel = new Carousel(element, {
      interval: false,
      keyboard: true,
      pause: false,
      touch: false,
      wrap: true
    });
    const slid = nextEvent(element, "slid.bs.carousel");
    carousel.next();
    await slid;
    assert(second.classList.contains("active"), "carousel did not activate the next item");
    assert(!first.classList.contains("active"), "carousel retained the previous active item");
    equal(lifecycle.join(","), "slide:0-1,slid:0-1", "carousel event order and indices");
    equal(element.querySelector("[data-bs-slide-to='1']")?.getAttribute("aria-current"), "true",
      "carousel indicator ARIA state");
    carousel.dispose();
  }]
]);
