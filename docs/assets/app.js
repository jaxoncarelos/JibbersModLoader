// Jibbers Mod SDK docs: guides (Markdown) and a searchable API reference (data/api.json, and
// data/raw.json for raw game classes, loaded on demand). No dependencies.
(() => {
  "use strict";
  const $ = (s) => document.querySelector(s);
  const main = $("#main");
  const input = $("#q");
  let api = null;
  let raw = null;
  let rawLoading = null;
  const guides = {};
  const kinds = {
    type: ["class", "static class", "struct", "enum", "interface", "delegate"],
    member: ["method", "property", "field", "constructor", "value"],
    game: ["symbol", "symbol field", "symbol method"],
    raw: ["raw class", "raw field", "raw method"],
  };
  const groupOf = (k) => Object.keys(kinds).find((g) => kinds[g].includes(k)) || "member";
  const filters = { type: true, member: true, game: true, raw: false };

  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const inline = (s) =>
    esc(s)
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, t, h) => `<a href="${h.startsWith("http") || h.startsWith("#") ? h : "#guide/" + h.replace(/\.md$/, "")}">${t}</a>`);

  // Minimal Markdown: headings, paragraphs, lists, fenced code, tables, quotes.
  function markdown(src) {
    const lines = src.replace(/\r/g, "").split("\n");
    let out = "";
    let i = 0;
    while (i < lines.length) {
      const l = lines[i];
      if (/^```/.test(l)) {
        const buf = [];
        i++;
        while (i < lines.length && !/^```/.test(lines[i])) buf.push(lines[i++]);
        i++;
        out += `<pre><code>${esc(buf.join("\n"))}</code></pre>`;
      } else if (/^#{1,4} /.test(l)) {
        const n = l.match(/^#+/)[0].length;
        const t = l.slice(n + 1);
        out += `<h${n} id="${t.toLowerCase().replace(/[^a-z0-9]+/g, "-")}">${inline(t)}</h${n}>`;
        i++;
      } else if (/^\|/.test(l)) {
        const rows = [];
        while (i < lines.length && /^\|/.test(lines[i])) rows.push(lines[i++]);
        const cells = (r) => r.replace(/^\||\|$/g, "").split("|").map((c) => c.trim());
        const head = cells(rows[0]);
        out += "<table><thead><tr>" + head.map((c) => `<th>${inline(c)}</th>`).join("") + "</tr></thead><tbody>";
        for (const r of rows.slice(2)) out += "<tr>" + cells(r).map((c) => `<td>${inline(c)}</td>`).join("") + "</tr>";
        out += "</tbody></table>";
      } else if (/^\s*([-*]|\d+\.) /.test(l)) {
        const ordered = /^\s*\d+\./.test(l);
        out += ordered ? "<ol>" : "<ul>";
        while (i < lines.length && /^\s*([-*]|\d+\.) /.test(lines[i])) {
          let item = lines[i].replace(/^\s*([-*]|\d+\.) /, "");
          i++;
          // continuation lines and nested code blocks
          while (i < lines.length && /^ {2,}\S/.test(lines[i]) && !/^\s*([-*]|\d+\.) /.test(lines[i])) item += " " + lines[i++].trim();
          let extra = "";
          while (i < lines.length && /^ {2,}```/.test(lines[i])) {
            const buf = [];
            i++;
            while (i < lines.length && !/^\s*```/.test(lines[i])) buf.push(lines[i++].replace(/^ {3}/, ""));
            i++;
            extra += `<pre><code>${esc(buf.join("\n"))}</code></pre>`;
            while (i < lines.length && lines[i].trim() === "") i++;
            while (i < lines.length && /^ {2,}\S/.test(lines[i]) && !/^\s*([-*]|\d+\.) /.test(lines[i])) extra += `<p>${inline(lines[i++].trim())}</p>`;
          }
          out += `<li>${inline(item)}${extra}</li>`;
        }
        out += ordered ? "</ol>" : "</ul>";
      } else if (/^> /.test(l)) {
        const buf = [];
        while (i < lines.length && /^> /.test(lines[i])) buf.push(lines[i++].slice(2));
        out += `<blockquote>${inline(buf.join(" "))}</blockquote>`;
      } else if (l.trim() === "") {
        i++;
      } else {
        const buf = [];
        while (i < lines.length && lines[i].trim() !== "" && !/^(#|```|\||\s*[-*] |\s*\d+\. |> )/.test(lines[i])) buf.push(lines[i++]);
        out += `<p>${inline(buf.join(" "))}</p>`;
      }
    }
    return out;
  }

  // Docs text from the XML comments: paragraphs and fenced code.
  function docText(s) {
    if (!s) return "";
    return s.split(/\n```\n?/).map((part, i) => (i % 2 ? `<pre><code>${esc(part.trim())}</code></pre>` : part.split(/\n\n+/).map((p) => `<p>${inline(p.replace(/\n/g, " "))}</p>`).join(""))).join("");
  }

  const fullName = (e) => (e.p ? e.p + "." + e.n : e.n);
  const entryHref = (e) => "#api/" + encodeURIComponent(fullName(e)) + (e.k.includes("method") || e.k === "constructor" ? "~" + encodeURIComponent(e.s) : "");
  const badge = (e) => `<span class="badge b-${groupOf(e.k)}">${esc(e.k)}</span>`;

  function all() {
    return raw && filters.raw ? api.entries.concat(raw.entries) : api.entries;
  }

  function score(e, terms, q) {
    const n = e.n.toLowerCase();
    const full = fullName(e).toLowerCase();
    let s = 0;
    if (n === q) s += 100;
    else if (n.startsWith(q)) s += 60;
    else if (n.includes(q)) s += 35;
    if (full.includes(q)) s += 20;
    for (const t of terms) {
      if (n.includes(t)) s += 12;
      else if (full.includes(t)) s += 8;
      else if ((e.s || "").toLowerCase().includes(t)) s += 5;
      else if ((e.d || "").toLowerCase().includes(t) || (e.real || "").toLowerCase().includes(t)) s += 3;
      else return 0;
    }
    if (groupOf(e.k) === "type" || e.k === "symbol") s += 4;
    if (groupOf(e.k) === "raw") s -= 6;
    return s;
  }

  function search(q) {
    q = q.trim().toLowerCase();
    if (!q) return [];
    const terms = q.split(/\s+/);
    const hits = [];
    for (const e of all()) {
      if (!filters[groupOf(e.k)]) continue;
      const s = score(e, terms, q);
      if (s > 0) hits.push([s, e]);
    }
    hits.sort((a, b) => b[0] - a[0] || a[1].n.length - b[1].n.length);
    return hits.map((h) => h[1]);
  }

  function resultList(list, limit = 200) {
    if (!list.length) return `<p class="empty">Nothing found.</p>`;
    return `<ul class="results">` + list.slice(0, limit).map((e) => `<li><a href="${entryHref(e)}">${badge(e)}<span class="rname">${esc(e.n)}</span> <span class="rparent">${esc(e.p)}</span><div class="rdoc">${esc((e.d || e.s || "").replace(/`/g, ""))}</div></a></li>`).join("") + `</ul>` + (list.length > limit ? `<p class="empty">${list.length - limit} more: refine the search.</p>` : "");
  }

  function filterBar(count) {
    const names = { type: "Types", member: "Members", game: "Game symbols", raw: "Raw game classes" };
    return `<div class="filters">` + Object.keys(names).map((k) => `<button class="chip ${filters[k] ? "on" : ""}" data-filter="${k}" aria-pressed="${filters[k]}">${names[k]}</button>`).join("") + `<span class="count">${count}</span></div>`;
  }

  async function showSearch(q) {
    if (filters.raw && !raw) await loadRaw();
    const hits = search(q);
    main.innerHTML = `<h1 class="sr">Search</h1>` + filterBar(`${hits.length} results`) + resultList(hits);
    mark(null);
  }

  function showSection(sec) {
    const list = api.entries.filter((e) => e.sec === sec && (groupOf(e.k) === "type" || e.k === "symbol"));
    const members = api.entries.filter((e) => e.sec === sec && !(groupOf(e.k) === "type" || e.k === "symbol"));
    main.innerHTML = `<div class="guide"><h1>${esc(sec)}</h1></div>` + resultList(list.concat(members), 400);
    mark("#sec/" + encodeURIComponent(sec));
  }

  function showEntry(name, sig) {
    const pool = all().concat(raw && !filters.raw ? raw.entries : []);
    const e = pool.find((x) => fullName(x) === name && (!sig || x.s === sig)) || pool.find((x) => fullName(x) === name);
    if (!e) {
      main.innerHTML = `<p class="empty">No entry ${esc(name)}.${raw ? "" : " It may be a raw game class: tick “Raw game classes” and search again."}</p>`;
      return;
    }
    let h = `<article class="entry"><div class="crumb">${esc(e.p)}</div><h1>${esc(e.n)}</h1>${badge(e)}${e.sec ? `<span class="rparent">${esc(e.sec)}</span>` : ""}`;
    if (e.s) h += `<pre class="sig"><code>${esc(e.s)}</code></pre>`;
    if (e.d) h += `<div class="doc">${docText(e.d)}</div>`;
    if (e.a && e.a.length) h += `<h2>Parameters</h2><table><thead><tr><th>Name</th><th>Type</th><th>Description</th></tr></thead><tbody>` + e.a.map((a) => `<tr><td><code>${esc(a.n)}</code></td><td><code>${esc(a.t)}</code></td><td>${inline(a.d)}</td></tr>`).join("") + `</tbody></table>`;
    if (e.r) h += `<h2>Returns</h2><div class="doc">${docText(e.r)}</div>`;
    if (e.rm) h += `<h2>Remarks</h2><div class="doc">${docText(e.rm)}</div>`;
    if (e.ex) h += `<h2>Example</h2><div class="doc">${docText(e.ex)}</div>`;
    if (e.real) h += `<dl><dt>Real name</dt><dd>${esc(e.real)}</dd><dt>Build</dt><dd>${esc(api.build)}</dd></dl>`;
    if (e.t && e.t.length) h += `<div class="tags">` + e.t.map((t) => `<span>${esc(t)}</span>`).join("") + `</div>`;
    const kids = pool.filter((x) => x.p === fullName(e));
    if (kids.length) h += `<h2>Members</h2>` + resultList(kids, 1000);
    main.innerHTML = h + `</article>`;
    mark(null);
  }

  async function showGuide(name) {
    if (!guides[name]) {
      const r = await fetch(`guides/${name}.md`);
      if (!r.ok) {
        main.innerHTML = `<p class="empty">No guide ${esc(name)}.</p>`;
        return;
      }
      guides[name] = await r.text();
    }
    main.innerHTML = `<article class="guide">${markdown(guides[name])}</article>`;
    mark("#guide/" + name);
  }

  function mark(href) {
    document.querySelectorAll(".nav a").forEach((a) => a.classList.toggle("on", a.getAttribute("href") === href));
  }

  async function loadRaw() {
    rawLoading ??= fetch("data/raw.json").then((r) => r.json()).then((j) => (raw = j));
    return rawLoading;
  }

  async function route() {
    const h = decodeURIComponent(location.hash.slice(1));
    $("#nav").classList.remove("open");
    if (h.startsWith("search/")) {
      const q = h.slice(7);
      if (input.value !== q) input.value = q;
      return showSearch(q);
    }
    if (h.startsWith("api/")) {
      const [n, s] = h.slice(4).split("~");
      if (!api.entries.some((x) => fullName(x) === n)) await loadRaw();
      return showEntry(n, s);
    }
    if (h.startsWith("sec/")) return showSection(h.slice(4));
    return showGuide(h.startsWith("guide/") ? h.slice(6) : "getting-started");
  }

  let timer = 0;
  input.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      const q = input.value;
      history.replaceState(null, "", q ? "#search/" + encodeURIComponent(q) : "#guide/getting-started");
      route();
    }, 120);
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "/" && document.activeElement !== input) {
      e.preventDefault();
      input.focus();
    }
  });
  main.addEventListener("click", async (e) => {
    const chip = e.target.closest("[data-filter]");
    if (!chip) return;
    filters[chip.dataset.filter] = !filters[chip.dataset.filter];
    await showSearch(input.value);
  });
  $("#menu").addEventListener("click", () => {
    const nav = $("#nav");
    nav.classList.toggle("open");
    $("#menu").setAttribute("aria-expanded", nav.classList.contains("open"));
  });
  window.addEventListener("hashchange", route);

  fetch("data/api.json")
    .then((r) => r.json())
    .then((j) => {
      api = j;
      const secs = {};
      for (const e of api.entries) if (e.sec) secs[e.sec] = (secs[e.sec] || 0) + 1;
      const order = Object.keys(secs).sort((a, b) => (a.startsWith("Game") - b.startsWith("Game")) || a.localeCompare(b));
      $("#sections").innerHTML = order.map((s) => `<li><a href="#sec/${encodeURIComponent(s)}">${esc(s)}<span>${secs[s]}</span></a></li>`).join("");
      $("#meta").textContent = `SDK ${api.sdk} · game build ${api.build.split(",")[0]} · generated ${api.generated}`;
      route();
    })
    .catch(() => (main.innerHTML = `<p class="empty">Could not load data/api.json. Serve this folder over HTTP (GitHub Pages, or <code>python -m http.server</code>).</p>`));
})();
