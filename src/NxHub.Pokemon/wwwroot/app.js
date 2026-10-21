"use strict";

// Trade bot page. Plain DOM, no build step. Everything shown from the bot (trainer names, messages)
// goes through textContent, never innerHTML.
(function () {
  const $ = (id) => document.getElementById(id);
  const api = (path) => new URL("api/" + path, document.baseURI).toString();
  const TOKEN_KEY = "nxhub-trade-token";

  let token = null;
  try { token = window.localStorage.getItem(TOKEN_KEY); } catch (e) { /* storage blocked: ask each visit */ }

  let me = null;
  let status = null;
  let timer = null;
  let setupLoaded = false;

  class ApiError extends Error {
    constructor(status, problem) {
      super((problem && (problem.detail || problem.title)) || "Request failed (" + status + ").");
      this.status = status;
      this.code = problem && problem.code;
    }
  }

  async function call(method, path, body) {
    const headers = { Accept: "application/json" };
    if (token) headers.Authorization = "Bearer " + token;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch(api(path), { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: "same-origin" });
    if (response.status === 204) return null;
    const data = await response.json().catch(() => null);
    if (!response.ok) throw new ApiError(response.status, data);
    return data;
  }

  function el(tag, props, children) {
    const node = document.createElement(tag);
    if (props) {
      for (const [key, value] of Object.entries(props)) {
        if (value === undefined || value === null) continue;
        if (key === "text") node.textContent = value;
        else if (key === "class") node.className = value;
        else node.setAttribute(key, value);
      }
    }
    for (const child of children || []) if (child) node.append(child);
    return node;
  }

  function show(id, visible) { $(id).hidden = !visible; }

  function showError(id, error) {
    const box = $(id);
    box.textContent = error ? error.message || String(error) : "";
    box.hidden = !error;
  }

  // ---------- sign in ----------
  async function boot() {
    try {
      me = await call("GET", "me");
    } catch (error) {
      if (error.status === 401) {
        show("signin", true);
        setChip("Signed out", "");
        return;
      }
      setChip("Unavailable", "bad");
      banner("The trade bot isn't answering. It may be restarting; this page retries every few seconds.");
      setTimeout(boot, 4000);
      return;
    }
    show("signin", false);
    show("request", true);
    show("requests", true);
    show("bot", me.isOwner);
    show("setup", me.isOwner);
    show("owner-extras", me.isOwner);
    show("all-label", me.isOwner);
    await refresh();
    schedule();
  }

  $("signin-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    token = $("token").value.trim();
    try { window.localStorage.setItem(TOKEN_KEY, token); } catch (e) { /* not remembered */ }
    await boot();
  });

  // ---------- polling ----------
  function schedule() {
    clearTimeout(timer);
    if (document.visibilityState === "visible") timer = setTimeout(tick, 2000);
  }

  async function tick() {
    try { await refresh(); } catch (e) { /* shown by refresh */ }
    schedule();
  }

  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible" && me) tick(); });

  async function refresh() {
    try {
      status = await call("GET", "status");
    } catch (error) {
      setChip("Unavailable", "bad");
      banner("The trade bot isn't answering. It may be restarting.");
      return;
    }
    renderStatus();
    if (status.sourceUrl) $("source").href = status.sourceUrl;
    const all = me.isOwner && $("show-all").checked;
    renderRequests(await call("GET", "requests" + (all ? "?all=true" : "")));
    if (me.isOwner) {
      if (!setupLoaded) await loadSetup();
      if ($("queue-details").open) renderQueue(await call("GET", "queue"));
      if ($("log-details").open) renderLog(await call("GET", "log?count=150"));
    }
  }

  function setChip(text, tone) {
    const chip = $("status-chip");
    chip.textContent = text;
    chip.className = "chip" + (tone ? " " + tone : "");
  }

  function banner(text) {
    const box = $("banner");
    box.textContent = text || "";
    box.hidden = !text;
  }

  function renderStatus() {
    if (status.restartPending) {
      setChip("Restarting", "warn");
    } else if (!status.configured) {
      setChip("Not set up", "warn");
    } else if (status.running) {
      setChip("Running", "ok");
    } else {
      setChip("Stopped", "");
    }

    if (!status.configured) {
      banner(status.problem || "The trade bot isn't set up yet.");
    } else if (status.handOver.lastError) {
      banner(status.handOver.lastError);
    } else if (!status.running) {
      banner(me.isOwner ? "The bot is stopped. Requests wait in the queue until you start it." : "The bot is offline right now. Your request will wait in the queue.");
    } else {
      banner("");
    }

    const bot = status.bots[0];
    $("fact-game").textContent = status.game || "–";
    $("fact-switch").textContent = bot ? bot.console : "–";
    $("fact-ign").textContent = bot && bot.name !== "SysBot.NET" ? bot.name : "Read when the bot starts";
    $("fact-handover").textContent = !status.handOver.enabled
      ? "Not linked"
      : status.handOver.held ? "Switch handed over to the bot" : "NX Hub has the Switch";
    $("fact-queue").textContent = status.queueLength === 0 ? "Empty" : status.queueLength + (status.queueLength === 1 ? " request" : " requests");
    $("fact-discord").textContent = status.discord ? "On" : "Off";

    $("start").disabled = !status.configured || status.running || status.restartPending;
    $("stop").disabled = !status.running;
    for (const id of ["trade-form", "info-form"]) {
      for (const button of $(id).querySelectorAll("button")) button.disabled = !status.configured;
    }
  }

  // ---------- bot control ----------
  $("start").addEventListener("click", async () => {
    const ok = window.confirm("Start the trade bot?\n\nIt will press buttons on your Switch and go online for link trades. Make sure the game is open and saved.");
    if (!ok) return;
    $("start").disabled = true;
    try {
      await call("POST", "bot/start");
    } catch (error) {
      window.alert(error.message);
    }
    await refresh();
  });

  $("stop").addEventListener("click", async () => {
    $("stop").disabled = true;
    try { await call("POST", "bot/stop"); } catch (error) { window.alert(error.message); }
    await refresh();
  });

  // ---------- requests ----------
  function selectTab(info) {
    $("tab-trade").setAttribute("aria-selected", String(!info));
    $("tab-info").setAttribute("aria-selected", String(info));
    show("trade-form", !info);
    show("info-form", info);
    showError("request-error", null);
  }

  $("tab-trade").addEventListener("click", () => selectTab(false));
  $("tab-info").addEventListener("click", () => selectTab(true));

  function readFileBase64(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result).split(",")[1] || "");
      reader.onerror = () => reject(new Error("The file couldn't be read."));
      reader.readAsDataURL(file);
    });
  }

  async function submit(form, send) {
    const button = form.querySelector("button[type=submit]");
    button.disabled = true;
    showError("request-error", null);
    try {
      await send();
      form.reset();
      await refresh();
    } catch (error) {
      showError("request-error", error);
    } finally {
      button.disabled = !(status && status.configured);
    }
  }

  $("trade-form").addEventListener("submit", (event) => {
    event.preventDefault();
    submit(event.currentTarget, async () => {
      const file = $("file").files[0];
      const body = { code: $("trade-code").value };
      if (file) body.file = await readFileBase64(file);
      else body.set = $("set").value;
      if (!body.file && !body.set.trim()) throw new Error("Paste a Showdown set or choose a Pokémon file.");
      await call("POST", "requests/trade", body);
    });
  });

  $("info-form").addEventListener("submit", (event) => {
    event.preventDefault();
    submit(event.currentTarget, () => call("POST", "requests/trainer-info", { code: $("info-code").value }));
  });

  $("show-all").addEventListener("change", () => refresh());

  const STATUS_TEXT = { queued: "Queued", starting: "Get ready", searching: "Searching", finished: "Done", canceled: "Cancelled" };
  const STATUS_TONE = { queued: "", starting: "warn", searching: "ok", finished: "ok", canceled: "bad" };

  function renderRequests(list) {
    show("requests-empty", list.length === 0);
    $("request-list").replaceChildren(...list.slice(0, 30).map(renderRequest));
  }

  function renderRequest(request) {
    const title = request.kind === "trainerInfo" ? "Trainer info" : request.pokemon || "Pokémon file";
    const latest = request.messages.length ? request.messages[request.messages.length - 1].text : "";
    const active = request.status === "queued" || request.status === "starting" || request.status === "searching";
    const sub = [new Date(request.createdAt).toLocaleString(), me.isOwner ? request.user : null, request.partner ? "Partner: " + request.partner : null]
      .filter(Boolean).join(" · ");

    const head = el("div", { class: "request-head" }, [
      el("div", null, [el("div", { class: "request-title", text: title }), el("div", { class: "request-sub", text: sub })]),
      el("span", { class: "chip " + (STATUS_TONE[request.status] || ""), text: STATUS_TEXT[request.status] || request.status }),
    ]);

    const parts = [head];
    if (active) parts.push(el("div", null, [el("div", { class: "request-sub", text: "Link code" }), el("div", { class: "code", text: request.code })]));
    const message = request.outcome && !active ? request.outcome : latest;
    if (message) parts.push(el("p", { class: "message", text: message }));
    if (request.received) parts.push(el("p", { class: "request-sub", text: "You sent: " + request.received }));
    for (const trainer of request.trainers) parts.push(renderTrainer(trainer));

    if (request.messages.length > 1) {
      parts.push(el("details", { class: "history" }, [
        el("summary", { text: "History" }),
        el("ol", null, request.messages.map((m) => el("li", { text: new Date(m.at).toLocaleTimeString() + " " + m.text }))),
      ]));
    }

    if (request.status === "queued") {
      const cancel = el("button", { class: "small", type: "button", text: "Cancel" });
      cancel.addEventListener("click", async () => {
        cancel.disabled = true;
        try { await call("DELETE", "requests/" + encodeURIComponent(request.id)); } catch (error) { window.alert(error.message); }
        await refresh();
      });
      parts.push(el("div", null, [cancel]));
    }

    return el("li", { class: "request" }, parts);
  }

  function renderTrainer(t) {
    const rows = [
      ["Trainer", t.name + " (" + t.gender + ")"],
      ["TID", t.tid],
      ["SID", t.sid],
      ["Game", t.game],
      ["Language", t.language],
      ["Read from", t.pokemon + (t.shiny ? " ✦ shiny" : "") + (t.legal ? "" : " (not legal)")],
    ];
    return el("table", { class: "trainers" }, [
      el("tbody", null, rows.map(([k, v]) => el("tr", null, [el("th", { text: k }), el("td", { text: v })]))),
    ]);
  }

  // ---------- owner: queue, log, setup ----------
  function renderQueue(list) {
    show("queue-empty", list.length === 0);
    $("queue-list").replaceChildren(...list.map((q) => el("li", null, [
      el("span", { text: q.position + ". " + q.user + " · " + q.type + (q.pokemon ? " · " + q.pokemon : "") }),
      el("span", { class: "muted", text: q.processing ? "Trading" : "Waiting" }),
    ])));
  }

  function renderLog(lines) {
    const box = $("log");
    const atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 8;
    box.textContent = lines.map((l) => new Date(l.at).toLocaleTimeString() + "  " + l.message).join("\n") || "No log yet.";
    if (atBottom) box.scrollTop = box.scrollHeight;
  }

  $("queue-details").addEventListener("toggle", () => refresh());
  $("log-details").addEventListener("toggle", () => refresh());

  async function loadSetup() {
    const data = await call("GET", "setup");
    setupLoaded = true;
    $("mode").replaceChildren(...data.games.map((g) => el("option", { value: g.mode, text: g.name })));
    if (data.setup.mode !== "none") $("mode").value = data.setup.mode;
    $("host").value = data.setup.host || "";
    $("port").value = String(data.setup.port || 6000);
    if (!data.setup.configured) $("setup-details").open = true;
  }

  function restarting() {
    setChip("Restarting", "warn");
    banner("Saved. The trade bot is restarting with the new settings…");
    setupLoaded = false;
  }

  $("setup-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    showError("setup-error", null);
    try {
      await call("PUT", "setup", { mode: $("mode").value, host: $("host").value.trim(), port: Number($("port").value) });
      restarting();
    } catch (error) {
      showError("setup-error", error);
    }
  });

  $("import").addEventListener("change", async () => {
    const file = $("import").files[0];
    if (!file) return;
    showError("setup-error", null);
    try {
      await call("POST", "setup/import", { json: await file.text() });
      restarting();
    } catch (error) {
      showError("setup-error", error);
    } finally {
      $("import").value = "";
    }
  });

  boot();
})();
