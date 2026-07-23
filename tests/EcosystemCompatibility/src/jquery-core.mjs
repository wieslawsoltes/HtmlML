import $ from "jquery";
import { assert, delay, equal, run } from "./harness.mjs";

const fixture = document.getElementById("fixture-root");

run([
  ["selectors traversal and positional filtering", () => {
    fixture.innerHTML = `<ul id="items"><li data-k="a">A</li><li data-k="b">B</li><li data-k="c">C</li></ul>`;
    const values = $("#items > li").filter(":odd").map((_index, node) => node.textContent).get();
    equal(values.join(","), "B", "jQuery selector/traversal result");
    equal($("[data-k='c']").closest("ul").attr("id"), "items", "closest ancestor");
  }],
  ["DOM insertion reorder detach and removal", () => {
    fixture.innerHTML = `<div id="source"><span id="a">A</span><span id="b">B</span></div><div id="target"></div>`;
    const retained = $("#b").detach();
    $("#target").append(retained).prepend($("#a"));
    equal($("#target").children().map((_index, node) => node.id).get().join(","), "a,b", "moved order");
    assert(retained[0].parentNode === document.getElementById("target"), "detach did not preserve node identity");
    $("#source").remove();
    equal(document.getElementById("source"), null, "removed source remains queryable");
  }],
  ["attributes properties classes and CSSOM compose", () => {
    fixture.innerHTML = `<input id="control" type="checkbox"><div id="box"></div>`;
    const control = $("#control");
    control.attr("data-state", "ready").prop("checked", true).addClass("active");
    equal(control.attr("data-state"), "ready", "attribute round trip");
    equal(control.prop("checked"), true, "property round trip");
    assert(control.hasClass("active"), "class mutation was not reflected");
    $("#box").css({ width: "37px", "margin-left": "5px" });
    equal($("#box").css("width"), "37px", "computed width");
  }],
  ["delegated namespaced events preserve target and payload", () => {
    fixture.innerHTML = `<div id="events"><button class="action">Go</button></div>`;
    const observations = [];
    $("#events").on("click.consumer", ".action", function (event, payload) {
      observations.push(`${this.className}:${event.target.tagName}:${payload}`);
    });
    $(".action").trigger("click", ["value"]);
    equal(observations.join("|"), "action:BUTTON:value", "delegated event observation");
    $("#events").off(".consumer");
    $(".action").trigger("click", ["ignored"]);
    equal(observations.length, 1, "namespaced event removal");
  }],
  ["form values serialize through the live control model", () => {
    fixture.innerHTML = `<form id="form"><input name="query" value="old"><select name="mode"><option value="a">A</option><option value="b">B</option></select></form>`;
    $("input[name=query]").val("new");
    $("select[name=mode]").val("b");
    equal($("#form").serialize(), "query=new&mode=b", "serialized form values");
  }],
  ["multiple-select values and repeated form fields serialize in tree order", () => {
    fixture.innerHTML = `<form id="multi-form"><select name="mode" multiple><option value="a">A</option><option value="b">B</option><option value="c">C</option></select><input name="tag" value="one"><input name="tag" value="two"></form>`;
    $("select[name=mode]").val(["b", "c"]);
    equal($("select[name=mode]").val().join(","), "b,c", "multiple select values");
    equal($("#multi-form").serialize(), "mode=b&mode=c&tag=one&tag=two", "repeated serialized values");
  }],
  ["deep clone preserves descendant data and registered events", () => {
    fixture.innerHTML = `<div id="clone-source"><button class="action">Clone</button></div>`;
    const observations = [];
    $("#clone-source .action").data("payload", { value: 7 }).on("click.clone", function () {
      observations.push($(this).data("payload").value);
    });
    const clone = $("#clone-source").clone(true, true).attr("id", "clone-target").appendTo(fixture);
    clone.find(".action").trigger("click");
    equal(observations.join(","), "7", "cloned descendant event and data");
    assert(clone[0] !== document.getElementById("clone-source"), "clone reused the source node");
  }],
  ["Deferred callbacks preserve resolution ordering", async () => {
    const deferred = $.Deferred();
    const values = [];
    deferred.then(value => values.push(`then:${value}`));
    deferred.done(value => values.push(`done:${value}`));
    deferred.resolve("ok");
    await delay(30);
    assert(values.includes("done:ok") && values.includes("then:ok"), `Deferred observations were ${values}`);
  }]
]);
