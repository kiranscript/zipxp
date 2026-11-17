(function () {
  "use strict";
  document.documentElement.classList.remove("no-js");
  var reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  /* ---------- Nav ---------- */
  var nav = $(".nav");
  if (nav) {
    var onScroll = function () { nav.classList.toggle("is-stuck", window.scrollY > 8); };
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    var menu = $(".nav__menu", nav);
    if (menu) {
      menu.addEventListener("click", function () {
        var open = nav.classList.toggle("is-open");
        menu.setAttribute("aria-expanded", String(open));
      });
      $$(".nav__drawer a", nav).forEach(function (a) {
        a.addEventListener("click", function () { nav.classList.remove("is-open"); menu.setAttribute("aria-expanded", "false"); });
      });
    }
  }

  /* ---------- Reveal on scroll + counters ---------- */
  function countUp(el) {
    var target = parseFloat(el.dataset.count), dec = (el.dataset.count.split(".")[1] || "").length;
    var fmt = function (v) { return v.toLocaleString("en-US", { minimumFractionDigits: dec, maximumFractionDigits: dec }); };
    if (reduce) { el.textContent = fmt(target); return; }
    var t0 = performance.now(), dur = 1400;
    (function tick(now) {
      var k = Math.min(1, (now - t0) / dur), e = 1 - Math.pow(1 - k, 3);
      el.textContent = fmt(target * e);
      if (k < 1) requestAnimationFrame(tick);
    })(t0);
  }
  if ("IntersectionObserver" in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (!en.isIntersecting) return;
        en.target.classList.add("is-in");
        $$("[data-count]", en.target).forEach(countUp);
        if (en.target.hasAttribute("data-count")) countUp(en.target);
        io.unobserve(en.target);
      });
    }, { threshold: 0.15, rootMargin: "0px 0px -40px 0px" });
    $$(".reveal, .step, [data-observe]").forEach(function (el) { io.observe(el); });
  } else {
    $$(".reveal").forEach(function (el) { el.classList.add("is-in"); });
  }

  /* ---------- Policy-gate playground ---------- */
  var play = $("#playground");
  if (play) {
    var worker = $(".worker", play);
    var title = $("#worker-title"), sub = $("#worker-sub");
    var rows = $$(".wq__row", play);
    var doneEl = $("#wq-done"), savedEl = $("#wq-saved");
    var pauseBtn = $("#wq-pause");
    var paused = false, idx = 0, done = 0, saved = 0, timer = 0;
    var files = [
      ["Projects\\assets\\atlas.psd", 18.4],
      ["Downloads\\logs\\server.log", 41.7],
      ["Documents\\export.xlsx", 2.1],
      ["Videos\\clip.mp4", -1],
      ["Projects\\vendor\\sdk.dll", 3.9],
      ["Downloads\\datasets\\events.csv", 26.2],
      ["Documents\\notes\\archive.json", 0.6],
      ["Pictures\\raw\\IMG_2041.cr3", -1]
    ];
    var reasons = {
      battery: ["Waiting: on battery", "This folder's policy only runs on AC power."],
      cpu: ["Waiting: system busy", "Sampled total CPU is above 85%. Checked again between files."],
      schedule: ["Waiting: outside schedule", "Folder schedule is 22:00–06:00 local time."],
      space: ["Waiting: low free space", "Below the 256 MiB free-space reserve."],
      quiet: ["Waiting: file still changing", "Quiet period: 60 s since the last write."]
    };

    function blocking() {
      return $$("input[data-gate]", play).filter(function (x) { return x.checked; }).map(function (x) { return x.dataset.gate; });
    }
    function render() {
      var b = blocking();
      $$(".gate", play).forEach(function (g) { g.classList.toggle("is-active", $("input", g).checked); });
      worker.classList.toggle("is-paused", paused);
      worker.classList.toggle("is-waiting", !paused && b.length > 0);
      if (paused) { title.textContent = "Paused"; sub.textContent = "Finishes the file in progress, then stops. Pending work stays persisted."; }
      else if (b.length) { title.textContent = reasons[b[0]][0]; sub.textContent = reasons[b[0]][1] + (b.length > 1 ? " (+" + (b.length - 1) + " more)" : ""); }
      else { title.textContent = "Running"; sub.textContent = "One file at a time, below-normal priority."; }
      pauseBtn.textContent = paused ? "Resume" : "Pause all";
    }
    function paint() {
      rows.forEach(function (row, r) {
        var f = files[(idx + r) % files.length];
        $("span", row).textContent = f[0];
        row.className = "wq__row";
        var em = $("em", row), prog = $(".prog", row);
        prog.style.transition = "none"; prog.style.width = "0";
        if (r === 0) { row.classList.add("is-run"); em.textContent = "compressing"; }
        else em.textContent = "queued";
      });
    }
    function step() {
      if (paused || blocking().length) { timer = setTimeout(step, 600); return; }
      var row = rows[0], prog = $(".prog", row), f = files[idx % files.length];
      void prog.offsetWidth;
      prog.style.transition = "width 1.5s linear"; prog.style.width = "100%";
      timer = setTimeout(function () {
        var em = $("em", row);
        if (f[1] < 0) { row.className = "wq__row is-skip"; em.textContent = "poor benefit"; }
        else { row.className = "wq__row is-done"; em.textContent = "−" + f[1] + " MiB"; saved += f[1]; }
        done++;
        doneEl.textContent = done;
        savedEl.textContent = saved.toFixed(1) + " MiB";
        timer = setTimeout(function () { idx++; paint(); timer = setTimeout(step, 250); }, 900);
      }, 1500);
    }
    play.addEventListener("change", render);
    $$(".gate", play).forEach(function (g) {
      g.addEventListener("click", function (e) {
        if (e.target.tagName === "INPUT") return;
        var inp = $("input", g); inp.checked = !inp.checked; render();
      });
    });
    pauseBtn.addEventListener("click", function () { paused = !paused; render(); });
    $("#wq-reset").addEventListener("click", function () {
      $$("input[data-gate]", play).forEach(function (x) { x.checked = false; });
      paused = false; done = 0; saved = 0; doneEl.textContent = "0"; savedEl.textContent = "0.0 MiB"; render();
    });
    render(); paint();
    if (reduce) { rows[0].className = "wq__row is-run"; }
    else {
      var started = false;
      new IntersectionObserver(function (en) {
        if (en[0].isIntersecting && !started) { started = true; timer = setTimeout(step, 400); }
      }).observe(play);
    }
  }

  /* ---------- Safety pipeline: light stages in sequence ---------- */
  var pipe = $("#pipeline");
  if (pipe) {
    var stages = $$(".pstage", pipe), at = -1, ptimer = 0;
    var advance = function () {
      at = (at + 1) % (stages.length + 1);
      stages.forEach(function (s, n) { s.classList.toggle("is-lit", n <= at && at < stages.length); });
      ptimer = setTimeout(advance, at === stages.length ? 900 : 1100);
    };
    if (reduce) stages.forEach(function (s) { s.classList.add("is-lit"); });
    else new IntersectionObserver(function (en) {
      clearTimeout(ptimer);
      if (en[0].isIntersecting) advance();
    }, { threshold: 0.3 }).observe(pipe);
  }

  /* ---------- Code tabs + copy ---------- */
  $$("[data-tabs]").forEach(function (box) {
    var tabs = $$("[role=tab]", box), panes = $$("[role=tabpanel]", box);
    function select(t) {
      tabs.forEach(function (x) { var on = x === t; x.setAttribute("aria-selected", String(on)); x.tabIndex = on ? 0 : -1; });
      panes.forEach(function (p) { p.hidden = p.id !== t.getAttribute("aria-controls"); });
    }
    tabs.forEach(function (t, n) {
      t.addEventListener("click", function () { select(t); });
      t.addEventListener("keydown", function (e) {
        var d = e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1 : 0;
        if (!d) return;
        var nt = tabs[(n + d + tabs.length) % tabs.length]; select(nt); nt.focus();
      });
    });
  });
  $$("[data-copy]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var scope = btn.closest("[data-tabs]") || btn.parentElement;
      var pre = $("pre:not([hidden])", scope);
      if (!pre) return;
      var text = $$(".line", pre).map(function (l) { return l.textContent; }).join("\n") || pre.textContent;
      var ok = function () { btn.textContent = "Copied"; setTimeout(function () { btn.textContent = "Copy"; }, 1500); };
      if (navigator.clipboard) navigator.clipboard.writeText(text).then(ok, function () {});
    });
  });

  /* ---------- Docs scroll-spy ---------- */
  var links = $$(".docs__nav a");
  if (links.length && "IntersectionObserver" in window) {
    var map = {};
    links.forEach(function (a) { map[a.getAttribute("href").slice(1)] = a; });
    var spy = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (!en.isIntersecting) return;
        links.forEach(function (a) { a.classList.remove("is-active"); });
        var a = map[en.target.id]; if (a) a.classList.add("is-active");
      });
    }, { rootMargin: "-20% 0px -70% 0px" });
    Object.keys(map).forEach(function (id) { var s = document.getElementById(id); if (s) spy.observe(s); });
  }

  $$("[data-year]").forEach(function (el) { el.textContent = new Date().getFullYear(); });
})();
