(() => {
  "use strict";

  const config = window.StreamXWorkbenchConfig;
  if (!config) return;

  const storageKey = "stream-x-workbench-preview-v5";
  const defaultState = {
    currentView: "workbench",
    activeTab: "home",
    activeProjectId: "germany-site",
    railExpanded: false,
    openSessionIds: ["germany-launch", "amazon-reconcile", "site-review"],
    theme: "light",
    density: "comfortable",
    hiddenModules: [],
    monitorExpanded: false,
    agentFilter: "all",
    inspectorTab: "steps",
    resourceFilter: "all",
    selectedFiles: {},
    mobileSessionPane: "conversation",
    drafts: {},
    onboardingComplete: false,
    onboardingAnswers: {}
  };

  const clone = value => typeof window.structuredClone === "function" ? window.structuredClone(value) : JSON.parse(JSON.stringify(value));
  const loadState = () => {
    try { return { ...defaultState, ...JSON.parse(localStorage.getItem(storageKey) || "{}") }; }
    catch { return { ...defaultState }; }
  };
  const state = loadState();
  const sessions = new Map(config.workSessions.map(session => [session.id, clone(session)]));
  const messagesBySession = new Map(config.workSessions.map(session => [session.id, clone(config.conversations[session.conversationKey].messages)]));
  const task = clone(config.currentTask);
  let onboardingStep = 0;
  let pendingAction = null;
  let typingTimer = null;

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const escapeHtml = (value = "") => String(value).replace(/[&<>"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;" }[character]));
  const icon = name => `<i data-lucide="${escapeHtml(name)}"></i>`;
  const refreshIcons = () => window.lucide?.createIcons({ attrs: { "aria-hidden": "true" } });
  const saveState = () => { try { localStorage.setItem(storageKey, JSON.stringify(state)); } catch { /* Offline preview can run without storage. */ } };
  const projectOf = session => config.projects.find(project => project.id === session?.projectId);
  const activeSession = () => sessions.get(state.activeTab);
  const activeProject = () => projectOf(activeSession()) || config.projects.find(project => project.id === state.activeProjectId) || config.projects[0];
  const primaryView = () => state.currentView === "work-session" ? "tasks" : state.currentView;
  const modeMeta = mode => ({
    expert: { label: "专家", fullLabel: "专家对话", icon: "messages-square" },
    work: { label: "工作", fullLabel: "工作对话", icon: "workflow" },
    meeting: { label: "会议", fullLabel: "会议对话", icon: "users-round" }
  }[mode] || { label: "工作", fullLabel: "工作对话", icon: "workflow" });

  function initialize() {
    applyDistribution();
    applyTheme();
    renderNavigation();
    applyNavigationState();
    renderSecondaryNavigation();
    renderHome();
    renderSecondaryViews();
    renderProjectChoice();
    renderAgentMonitor();
    renderOnboarding();
    bindEvents();
    if (state.activeTab !== "home" && sessions.has(state.activeTab)) switchWorkTab(state.activeTab, false);
    else navigate(state.currentView === "work-session" ? "workbench" : state.currentView, false);
    document.querySelector("#appShell").classList.toggle("monitor-open", state.monitorExpanded);
    $("#agentMonitorToggle").setAttribute("aria-expanded", String(state.monitorExpanded));
    updateClock();
    window.setInterval(updateClock, 1000);
    refreshIcons();

    if (!state.onboardingComplete && !new URLSearchParams(location.search).has("preview")) {
      window.setTimeout(() => openDialog($("#onboardingDialog")), 420);
    }
  }

  function applyDistribution() {
    document.title = `${config.distribution.productName} · 多项目${config.distribution.productSubtitle}`;
    $("#brandName").textContent = config.distribution.productName;
    $("#brandSubtitle").textContent = config.distribution.productSubtitle;
    $("#brandIcon").src = config.distribution.icon;
    $("#brandIcon").alt = `${config.distribution.productName} 标志`;
    $("#todayLabel").textContent = new Intl.DateTimeFormat("zh-CN", { weekday: "long", month: "long", day: "numeric" }).format(new Date());
  }

  function applyTheme() { document.documentElement.dataset.theme = state.theme; }

  function renderNavigation() {
    const regular = config.navigation.filter(item => !item.advanced);
    const advanced = config.navigation.filter(item => item.advanced);
    const itemTemplate = item => `<button class="nav-item" type="button" data-nav-target="${item.id}" aria-current="${primaryView() === item.id ? "page" : "false"}" aria-label="${escapeHtml(item.label)}" title="${escapeHtml(item.label)}">${icon(item.icon)}<span class="nav-label">${escapeHtml(item.label)}</span>${item.count ? `<span class="nav-count">${item.count}</span>` : ""}</button>`;
    $("#navList").innerHTML = `${regular.map(itemTemplate).join("")}<span class="nav-divider" aria-hidden="true"></span>${advanced.map(itemTemplate).join("")}`;
  }

  function applyNavigationState() {
    $("#appShell").classList.toggle("rail-expanded", state.railExpanded);
    $("#railExpandToggle").setAttribute("aria-expanded", String(state.railExpanded));
    $("#railExpandToggle").title = state.railExpanded ? "收起一级导航" : "展开一级导航";
    $("#railExpandToggle .rail-item-label").textContent = state.railExpanded ? "收起导航" : "展开导航";
  }

  function projectListTemplate() {
    return config.projects.map(project => `<button class="context-project" type="button" data-project-context="${project.id}" data-context-search-item aria-selected="${project.id === activeProject().id}"><span class="context-project-icon">${icon(project.icon)}</span><span class="context-project-copy"><strong>${escapeHtml(project.name)}</strong><small>${project.activeTasks} 项工作 · ${project.activeAgents} 个 Agent</small></span><span class="project-status-dot ${project.status === "执行中" ? "campaign" : ""}"></span></button>`).join("");
  }

  function projectDirectoryTemplate(project) {
    return `<section class="project-files-section" aria-labelledby="resourcesTitle">
      <header class="context-section-title project-files-heading"><div><small>项目工作目录</small><h2 id="resourcesTitle">${escapeHtml(project.name)}</h2></div><div><button class="rail-icon-button" id="addResourceButton" type="button" title="加入项目文件" aria-label="加入项目文件">${icon("paperclip")}</button><button class="rail-icon-button" id="refreshResourcesButton" type="button" title="刷新本地目录" aria-label="刷新本地目录">${icon("refresh-cw")}</button></div></header>
      <button class="project-root" id="projectRootButton" type="button" title="打开项目根目录">${icon("folder-root")}<span><strong id="projectRootName"></strong><small id="projectRootPath"></small></span></button>
      <div class="resource-toolbar"><button type="button" aria-pressed="${state.resourceFilter === "all"}" data-resource-filter="all">全部</button><button type="button" aria-pressed="${state.resourceFilter === "outputs"}" data-resource-filter="outputs">仅产出</button></div>
      <div class="resource-tree" id="resourceTree"></div><section class="file-detail" id="fileDetail" aria-label="选中文件详情"></section>
    </section>`;
  }

  function contextSearchTemplate(placeholder) {
    return `<label class="context-search">${icon("search")}<span class="sr-only">${escapeHtml(placeholder)}</span><input type="search" data-context-search placeholder="${escapeHtml(placeholder)}"></label>`;
  }

  function sessionListTemplate(limit = 99) {
    return [...sessions.values()].slice(0, limit).map(session => {
      const project = projectOf(session);
      const mode = modeMeta(session.mode);
      return `<button class="context-session" type="button" data-open-session="${session.id}" data-session-mode="${session.mode}" data-context-search-item aria-selected="${session.id === state.activeTab}"><span class="context-session-icon ${session.mode}">${icon(mode.icon)}</span><span><strong>${escapeHtml(session.shortTitle)}</strong><small>${escapeHtml(project.name)} · ${escapeHtml(session.status)}</small></span><em>${session.progress}%</em></button>`;
    }).join("");
  }

  function renderSecondaryNavigation() {
    const view = primaryView();
    const project = activeProject();
    const definitions = {
      workbench: ["工作台", `${config.projects.length} 个项目 · ${config.workSessions.length} 项工作`],
      projects: ["经营项目", `${config.projects.length} 个项目并行`],
      tasks: ["业务任务", activeSession() ? `${project.name} · ${modeMeta(activeSession().mode).label}模式` : `${config.workSessions.length} 项活跃工作`],
      assets: ["业务资产", `${project.name} · ${project.files} 个文件`],
      conversations: ["对话记录", "专家 · 工作 · 会议"],
      apps: ["应用与连接", `${config.modules.length} 个业务模块`],
      execution: ["执行中心", `${config.agentRuns.length} 个 Agent 实例`],
      settings: ["设置", "工作台与系统配置"]
    };
    const [title, subtitle] = definitions[view] || definitions.workbench;
    $("#contextPanelTitle").textContent = title;
    $("#contextPanelSubtitle").textContent = subtitle;
    const body = $("#contextSidebarBody");
    body.className = "context-sidebar-body";

    if (view === "projects") {
      body.innerHTML = `<div class="context-composite">${contextSearchTemplate("搜索经营项目")}<section class="project-context-section"><header class="context-section-title"><h2>全部项目</h2><span>${config.projects.length}</span></header><div class="context-project-list">${projectListTemplate()}</div></section>${projectDirectoryTemplate(project)}</div>`;
      renderResources(project);
    } else if (view === "tasks") {
      body.innerHTML = `<div class="context-composite tasks-context">${contextSearchTemplate("搜索业务任务")}<section class="context-session-section"><header class="context-section-title"><h2>正在进行</h2><span>${sessions.size}</span></header><div class="context-session-list">${sessionListTemplate()}</div></section>${projectDirectoryTemplate(project)}</div>`;
      renderResources(project);
    } else if (view === "assets") {
      body.innerHTML = `<div class="context-composite assets-context">${contextSearchTemplate("搜索项目或文件")}<section class="project-context-section compact"><header class="context-section-title"><h2>资产所属项目</h2><span>${config.projects.length}</span></header><div class="context-project-list">${projectListTemplate()}</div></section>${projectDirectoryTemplate(project)}</div>`;
      renderResources(project);
    } else if (view === "conversations") {
      body.classList.add("is-scrollable");
      body.innerHTML = `${contextSearchTemplate("搜索对话记录")}<section class="secondary-section"><header class="context-section-title"><h2>协作模式</h2></header><div class="mode-summary"><button type="button" data-conversation-mode="expert" aria-pressed="false"><span class="expert">${icon("messages-square")}</span><strong>专家</strong><small>分析与诊断</small></button><button type="button" data-conversation-mode="work" aria-pressed="false"><span>${icon("workflow")}</span><strong>工作</strong><small>计划与执行</small></button><button type="button" data-conversation-mode="meeting" aria-pressed="false"><span class="meeting">${icon("users-round")}</span><strong>会议</strong><small>讨论与决策</small></button></div></section><section class="secondary-section"><header class="context-section-title"><h2>最近对话</h2><span>${sessions.size}</span></header><div class="context-session-list">${sessionListTemplate()}</div></section>`;
    } else if (view === "apps") {
      body.classList.add("is-scrollable");
      body.innerHTML = `${contextSearchTemplate("搜索业务功能")}<section class="secondary-section"><header class="context-section-title"><h2>行业模块</h2><span>${config.modules.length}</span></header><div class="context-module-list">${config.modules.map(module => `<button type="button" data-context-search-item data-context-toast="${escapeHtml(module.title)}"><span>${icon(module.icon)}</span><span><strong>${escapeHtml(module.title)}</strong><small>${escapeHtml(module.subtitle)}</small></span>${icon("chevron-right")}</button>`).join("")}</div></section><section class="secondary-section"><header class="context-section-title"><h2>已连接</h2><span>3</span></header><div class="connection-summary"><span>Shopify</span><span>Amazon DE</span><span>Instagram</span></div></section>`;
    } else if (view === "execution") {
      const contextAgents = config.agentRuns.filter(agent => state.agentFilter === "all" || state.agentFilter === "attention" && agent.attention || state.agentFilter === "running" && ["运行中", "会议中"].includes(agent.status));
      body.classList.add("is-scrollable");
      body.innerHTML = `${contextSearchTemplate("搜索 Agent 或工作")}<section class="execution-mini-summary"><button type="button" data-agent-filter="all" aria-pressed="${state.agentFilter === "all"}"><strong>8</strong><span>全部</span></button><button type="button" data-agent-filter="running" aria-pressed="${state.agentFilter === "running"}"><strong>5</strong><span>运行中</span></button><button type="button" data-agent-filter="attention" aria-pressed="${state.agentFilter === "attention"}"><strong>2</strong><span>需处理</span></button></section><section class="secondary-section"><header class="context-section-title"><h2>Agent 实例</h2><span>${contextAgents.length}</span></header><div class="context-agent-list">${contextAgents.map(agent => `<button type="button" data-agent-session="${agent.sessionId}" data-context-search-item><span class="project-status-dot ${agent.attention ? "campaign" : ""}"></span><span><strong>${escapeHtml(agent.name)}</strong><small>${escapeHtml(agent.project)} · ${escapeHtml(agent.status)}</small></span><em>${agent.progress}%</em></button>`).join("")}</div></section>`;
    } else if (view === "settings") {
      body.classList.add("is-scrollable");
      const settings = [["workbench", "layout-dashboard", "工作台与初始化"], ["projects", "folder-root", "项目文件"], ["storage", "database", "对话与存储"]];
      body.innerHTML = `<section class="secondary-section settings-context"><header class="context-section-title"><h2>配置范围</h2></header><div class="secondary-nav-list">${settings.map(([id, itemIcon, label], index) => `<button type="button" data-settings-side="${id}" aria-current="${index === 0 ? "page" : "false"}">${icon(itemIcon)}<span>${label}</span>${icon("chevron-right")}</button>`).join("")}</div></section>`;
    } else {
      body.classList.add("is-scrollable");
      body.innerHTML = `<section class="secondary-section workbench-context"><header class="context-section-title"><h2>工作台视图</h2></header><div class="secondary-nav-list"><button type="button" data-nav-target="workbench" aria-current="page">${icon("layout-dashboard")}<span>全部项目</span><em>3</em></button><button type="button" data-nav-target="tasks">${icon("circle-alert")}<span>等待我处理</span><em>2</em></button><button type="button" data-nav-target="assets">${icon("file-check-2")}<span>最近产出</span><em>6</em></button></div></section><section class="secondary-section"><header class="context-section-title"><h2>继续工作</h2><span>${sessions.size}</span></header><div class="context-session-list">${sessionListTemplate(4)}</div></section><section class="secondary-section"><header class="context-section-title"><h2>项目运行</h2><span>${config.projects.length}</span></header><div class="workbench-project-summary">${config.projects.map(item => `<button type="button" data-nav-target="projects" data-project-context="${item.id}" data-context-search-item><span class="project-status-dot ${item.status === "执行中" ? "campaign" : ""}"></span><span><strong>${escapeHtml(item.name)}</strong><small>${item.activeTasks} 工作 · ${item.activeAgents} Agent</small></span></button>`).join("")}</div></section>`;
    }
    refreshIcons();
  }

  function selectProject(projectId) {
    const project = config.projects.find(item => item.id === projectId);
    if (!project) return;
    state.activeProjectId = project.id;
    const session = activeSession();
    if (session && session.projectId !== project.id) {
      const targetSession = project.sessionIds.map(id => sessions.get(id)).find(Boolean);
      if (targetSession) { switchWorkTab(targetSession.id); return; }
      navigate("workbench");
    }
    renderSecondaryNavigation();
    saveState();
  }

  function renderHome() {
    renderPortfolioSummary();
    renderProjectBoard();
    renderRunningWork();
    renderQuickActions();
    renderModules();
    renderWorkTabs();
  }

  function renderPortfolioSummary() {
    const metrics = [
      { icon: "briefcase-business", label: "运行项目", value: "3", detail: "6 项活跃工作" },
      { icon: "bot", label: "工作中智能体", value: "8", detail: "5 运行 · 2 需处理" },
      { icon: "folder-tree", label: "项目文件", value: "710", detail: "5.1 GB 本地资源" },
      { icon: "circle-alert", label: "等待你处理", value: "2", detail: "发布确认 · 素材确认" }
    ];
    $("#portfolioSummary").innerHTML = metrics.map(metric => `<article class="portfolio-metric"><span class="metric-icon">${icon(metric.icon)}</span><span class="metric-copy"><small>${metric.label}</small><strong>${metric.value}</strong><span>${metric.detail}</span></span></article>`).join("");
  }

  function renderProjectBoard() {
    $("#projectBoard").innerHTML = config.projects.map((project, index) => {
      const projectSessions = config.workSessions.filter(session => session.projectId === project.id).slice(0, 2);
      return `<article class="project-card ${index === 0 ? "primary" : ""}">
        <header class="project-card-header"><span class="project-icon">${icon(project.icon)}</span><span class="project-heading"><strong>${escapeHtml(project.name)}</strong><small>${escapeHtml(project.type)} · ${escapeHtml(project.market)}</small></span><span class="project-state"><span class="status-dot running"></span>${escapeHtml(project.status)}</span></header>
        <div class="project-stats"><span><small>活跃工作</small><strong>${project.activeTasks}</strong></span><span><small>智能体</small><strong>${project.activeAgents}</strong></span><span><small>项目文件</small><strong>${project.files}</strong></span></div>
        <div class="project-work">${projectSessions.map(session => `<div class="project-work-item"><span class="project-work-copy"><strong>${escapeHtml(session.shortTitle)}</strong><small>${escapeHtml(session.status)} · ${session.progress}% · ${session.agentCount} 个智能体</small></span><button class="icon-button" type="button" data-open-session="${session.id}" title="进入工作" aria-label="进入${escapeHtml(session.shortTitle)}">${icon("arrow-up-right")}</button></div>`).join("")}</div>
        <footer class="project-card-footer"><span title="${escapeHtml(project.path)}">${icon("folder-root")}${escapeHtml(project.path)}</span><span>${project.storage} · ${escapeHtml(project.updated)}</span></footer>
      </article>`;
    }).join("");
  }

  function renderRunningWork() {
    $("#runningWorkList").innerHTML = config.workSessions.map(session => {
      const project = projectOf(session);
      const mode = modeMeta(session.mode);
      return `<article class="running-work"><span class="work-mode-icon ${session.mode}">${icon(mode.icon)}</span><span class="running-work-main"><strong>${escapeHtml(session.title)}</strong><small>${escapeHtml(mode.label)}模式 · ${escapeHtml(session.status)} · ${escapeHtml(session.updated)}</small></span><span class="running-work-context"><span>${escapeHtml(project.name)}</span><small>${escapeHtml(session.taskId)}</small></span><span class="mini-progress"><span><i style="width:${session.progress}%"></i></span><em>${session.progress}%</em></span><span class="running-work-agents">${icon("users")} ${session.agentCount} 个</span><button class="icon-button" type="button" data-open-session="${session.id}" title="进入工作" aria-label="进入${escapeHtml(session.shortTitle)}">${icon("arrow-right")}</button></article>`;
    }).join("");
  }

  function renderQuickActions() {
    $("#quickActions").innerHTML = config.quickActions.map(action => `<button class="quick-action" type="button" data-action-id="${action.id}"><span>${icon(action.icon)}</span><span class="quick-action-copy"><strong>${escapeHtml(action.label)}</strong><small>${escapeHtml(action.description)}</small></span>${icon("arrow-up-right")}</button>`).join("");
  }

  function renderModules() {
    const modules = config.modules.filter(module => !state.hiddenModules.includes(module.id));
    const grid = $("#moduleGrid");
    grid.className = `module-grid ${state.density === "compact" ? "compact" : ""}`;
    grid.innerHTML = modules.map(module => `<article class="business-module"><header class="module-header"><span class="module-icon">${icon(module.icon)}</span><span class="module-title"><strong>${escapeHtml(module.title)}</strong><small>${escapeHtml(module.subtitle)}</small></span><span class="module-summary">${escapeHtml(module.summary)}</span></header><div class="module-actions">${module.actions.map(action => `<button class="module-action" type="button" data-module-action="${module.id}:${action.id}">${icon(action.icon)}${escapeHtml(action.label)}</button>`).join("")}</div></article>`).join("");
    refreshIcons();
  }

  function renderWorkTabs() {
    const home = `<button class="work-tab" type="button" data-work-tab="home" aria-selected="${state.activeTab === "home"}">${icon("layout-dashboard")}<span class="work-tab-title">全部项目</span></button>`;
    const opened = state.openSessionIds.filter(id => sessions.has(id)).map(id => {
      const session = sessions.get(id);
      const projectClass = session.projectId === "amazon-de" ? "amazon" : session.projectId === "summer-campaign" ? "campaign" : "";
      return `<button class="work-tab" type="button" data-work-tab="${id}" aria-selected="${state.activeTab === id}" title="${escapeHtml(session.title)}"><span class="work-tab-project ${projectClass}"></span><span class="work-tab-title">${escapeHtml(session.shortTitle)}</span>${session.unread ? `<span class="work-tab-unread">${session.unread}</span>` : ""}<span class="work-tab-close" role="button" tabindex="0" data-close-session="${id}" aria-label="关闭${escapeHtml(session.shortTitle)}">${icon("x")}</span></button>`;
    }).join("");
    $("#workTabs").innerHTML = home + opened;
    refreshIcons();
  }

  function navigate(view, persist = true) {
    const target = config.navigation.some(item => item.id === view) ? view : "workbench";
    state.currentView = target;
    if (target === "workbench") state.activeTab = "home";
    else state.activeTab = "";
    $$(".view").forEach(section => section.classList.toggle("is-active", section.dataset.view === target));
    $("#mainContent").scrollTop = 0;
    renderNavigation();
    renderWorkTabs();
    renderSecondaryNavigation();
    closeMobileNavigation();
    if (persist) saveState();
    refreshIcons();
  }

  function switchWorkTab(tabId, persist = true) {
    if (tabId === "home") { navigate("workbench", persist); return; }
    const session = sessions.get(tabId);
    if (!session) return;
    if (!state.openSessionIds.includes(tabId)) state.openSessionIds.push(tabId);
    state.activeTab = tabId;
    state.activeProjectId = session.projectId;
    state.currentView = "work-session";
    session.unread = 0;
    $$(".view").forEach(section => section.classList.toggle("is-active", section.id === "view-work-session"));
    renderNavigation();
    renderWorkTabs();
    renderSecondaryNavigation();
    renderWorkSession(session);
    if (persist) saveState();
  }

  function openSession(sessionId) { switchWorkTab(sessionId); }

  function closeSession(sessionId) {
    state.openSessionIds = state.openSessionIds.filter(id => id !== sessionId);
    if (state.activeTab === sessionId) switchWorkTab("home");
    else { renderWorkTabs(); saveState(); }
  }

  function renderWorkSession(session) {
    const project = projectOf(session);
    const mode = modeMeta(session.mode);
    $("#sessionModeMark").className = `session-mode-mark ${session.mode}`;
    $("#sessionModeMark").innerHTML = icon(mode.icon);
    $("#sessionBreadcrumb").textContent = `${project.name} / ${mode.label}模式`;
    $("#sessionTitle").textContent = session.title;
    $("#sessionObjective").textContent = session.objective;
    $("#sessionStatus").innerHTML = `<span class="status-dot running"></span>${escapeHtml(session.status)} · ${session.progress}%`;
    $("#sessionProjectChip").innerHTML = `${icon(project.icon)}${escapeHtml(project.name)}`;
    $("#sessionTaskChip").innerHTML = `${icon("list-checks")}${escapeHtml(session.taskId)}`;
    $("#sessionPath").innerHTML = `${icon("folder-root")}${escapeHtml(project.path)}`;
    $("#conversationModeLabel").textContent = mode.fullLabel;
    $("#conversationTitle").textContent = session.mode === "meeting" ? "多角色围绕当前议题讨论" : session.mode === "expert" ? "与专家分析当前问题" : "与任务协作组解决当前工作";
    renderParticipants(session);
    renderSecondaryNavigation();
    renderConversation(session);
    renderInspector(session);
    $(".work-session-layout").dataset.mobilePane = state.mobileSessionPane;
    $$('[data-session-mobile-pane]').forEach(button => button.setAttribute("aria-selected", String(button.dataset.sessionMobilePane === state.mobileSessionPane)));
    refreshIcons();
  }

  function renderParticipants(session) {
    const agents = config.agentRuns.filter(agent => agent.sessionId === session.id);
    const names = agents.length ? agents.map(agent => agent.name) : [modeMeta(session.mode).label + "协作者"];
    $("#participantStack").innerHTML = names.slice(0, 3).map(name => `<span class="participant-avatar" title="${escapeHtml(name)}">${escapeHtml(name.slice(0, 1))}</span>`).join("") + (names.length > 3 ? `<span class="participant-avatar participant-more">+${names.length - 3}</span>` : "");
  }

  function renderResources(project) {
    if (!project || !$("#resourceTree")) return;
    let folders = project.folders || [];
    if (state.resourceFilter === "outputs") folders = folders.filter(folder => /产出/.test(folder.label));
    $("#resourcesTitle").textContent = project.name;
    $("#projectRootName").textContent = project.path.split("\\").filter(Boolean).at(-1) || project.name;
    $("#projectRootPath").textContent = project.path;
    $("#projectRootButton").title = `打开 ${project.path}`;
    $("#resourceTree").innerHTML = folders.map(folder => `<section class="folder-group"><button class="folder-button" type="button" data-folder-id="${project.id}:${folder.id}" aria-expanded="true">${icon(folder.icon)}<strong>${escapeHtml(folder.label)}</strong><span class="folder-count">${folder.count}</span>${icon("chevron-down")}</button><div class="folder-items">${folder.items.map(file => `<button class="file-button" type="button" data-file-id="${file.id}" aria-selected="${state.selectedFiles[project.id] === file.id}">${icon(file.icon)}<span class="file-name">${escapeHtml(file.name)}</span></button>`).join("")}</div></section>`).join("");
    $$('[data-resource-filter]').forEach(button => button.setAttribute("aria-pressed", String(button.dataset.resourceFilter === state.resourceFilter)));
    renderFileDetail(project);
  }

  function findFile(project, fileId) {
    return (project?.folders || []).flatMap(folder => folder.items).find(file => file.id === fileId);
  }

  function renderFileDetail(project) {
    if (!$("#fileDetail")) return;
    const file = findFile(project, state.selectedFiles[project.id]);
    const detail = $("#fileDetail");
    if (!file) { detail.className = "file-detail empty"; detail.innerHTML = `<span>${icon("mouse-pointer-2")}选择文件查看位置、类型与任务引用</span>`; return; }
    const canReference = activeSession()?.projectId === project.id;
    detail.className = "file-detail";
    detail.innerHTML = `<div class="file-detail-header"><span>${icon(file.icon)}</span><span class="file-detail-copy"><strong>${escapeHtml(file.name)}</strong><small>${escapeHtml(file.type)} · ${escapeHtml(file.size)} · ${escapeHtml(file.updated)}</small></span></div><div class="file-detail-actions">${canReference ? `<button type="button" data-use-selected-file>${icon("at-sign")}引用到对话</button>` : ""}<button type="button" data-open-selected-file>${icon("external-link")}打开文件</button></div>`;
  }

  function renderConversation(session) {
    const preset = config.conversations[session.conversationKey];
    const project = projectOf(session);
    const file = findFile(project, state.selectedFiles[project.id]);
    const contexts = [project.name, session.taskId, ...(file ? [file.name] : preset.contexts.slice(1, 2))];
    $("#contextStrip").innerHTML = contexts.map((context, index) => `<span class="context-chip">${icon(index === 0 ? project.icon : index === 1 ? "list-checks" : "paperclip")}${escapeHtml(context)}</span>`).join("");
    $("#messageStream").innerHTML = (messagesBySession.get(session.id) || []).map(messageTemplate).join("");
    $("#suggestionStrip").innerHTML = preset.suggestions.map(suggestion => `<button type="button" data-suggestion="${escapeHtml(suggestion)}">${escapeHtml(suggestion)}</button>`).join("");
    $("#messageInput").value = state.drafts[session.id] || "";
    $("#messageInput").placeholder = session.mode === "work" ? "告诉 AI 要修改的方案、步骤或输入…" : session.mode === "meeting" ? "插话、追问或要求调整议题…" : "继续提问，或要求把建议转为工作…";
    requestAnimationFrame(() => { const stream = $("#messageStream"); stream.scrollTop = stream.scrollHeight; });
  }

  function messageTemplate(message) {
    const avatar = message.role === "user" ? "你" : message.author.slice(0, 1);
    return `<article class="message-group ${escapeHtml(message.role)}"><span class="message-avatar">${escapeHtml(avatar)}</span><div class="message-body"><div class="message-meta"><strong>${escapeHtml(message.author)}</strong><span>${escapeHtml(message.time)}</span></div><div class="message-bubble">${escapeHtml(message.content).replace(/\n/g, "<br>")}</div>${message.evidence ? `<div class="message-evidence">${escapeHtml(message.evidence)}</div>` : ""}${message.change ? `<div class="message-change">${icon("list-restart")}${escapeHtml(message.change)}</div>` : ""}</div></article>`;
  }

  function renderInspector(session) {
    $$('[data-inspector-tab]').forEach(button => button.setAttribute("aria-selected", String(button.dataset.inspectorTab === state.inspectorTab)));
    if (state.inspectorTab === "outputs") { renderOutputs(session); return; }
    if (state.inspectorTab === "agents") { renderSessionAgents(session); return; }
    const steps = stepsForSession(session);
    const labels = { completed: "已完成", running: "进行中", queued: "待执行", approval: "需确认", skipped: "已跳过" };
    $("#inspectorContent").innerHTML = `<div class="inspector-section-heading"><div><h3>${session.mode === "meeting" ? "会议议程" : session.mode === "expert" ? "分析步骤" : "业务步骤"}</h3><p>对话修改后自动同步</p></div><button class="icon-button" type="button" data-plan-history title="变更记录" aria-label="计划变更记录">${icon("history")}</button></div><ol class="plan-steps">${steps.map(step => `<li class="plan-step ${step.status}${step.isNew ? " is-new" : ""}"><span class="step-index"></span><span class="step-copy"><strong>${escapeHtml(step.label)}</strong><small>${escapeHtml(step.detail)}</small></span><span class="step-status">${labels[step.status] || step.status}</span></li>`).join("")}</ol>`;
  }

  function stepsForSession(session) {
    if (session.id === "germany-launch" || session.mode === "work" && session.taskId.startsWith("TASK-NEW")) return task.steps;
    if (session.id === "social-distribution") return [
      { label: "完成渠道内容适配", detail: "14 条内容，移动端格式", status: "completed" },
      { label: "编排 Pinterest 发布队列", detail: "6 / 8 条已完成", status: "running" },
      { label: "确认 Instagram Reel 封面", detail: "2 个版本等待选择", status: "approval" },
      { label: "执行外部发布并记录结果", detail: "确认后继续", status: "queued" }
    ];
    if (session.mode === "expert") return [
      { label: "读取结算与订单资源", detail: "已加载 2 个本地文件", status: "completed" },
      { label: "定位差异来源", detail: "正在核对退款与费用", status: "running" },
      { label: "整理证据与处理建议", detail: "等待分析结果", status: "queued" }
    ];
    return [
      { label: "确认上线评审议题", detail: "合规、支付、移动端", status: "completed" },
      { label: "收集各角色意见", detail: "4 个角色参与", status: "running" },
      { label: "处理异议与行动项", detail: "2 项待决议", status: "approval" },
      { label: "形成最终上线结论", detail: "等待行动项完成", status: "queued" }
    ];
  }

  function renderOutputs(session) {
    $("#inspectorContent").innerHTML = `<div class="inspector-section-heading"><div><h3>工作产出</h3><p>产出写入项目本地目录</p></div><button class="icon-button" type="button" data-open-output-folder title="打开产出目录" aria-label="打开产出目录">${icon("folder-open")}</button></div><div class="output-list">${(session.outputs || []).map(output => `<article class="output-item"><span class="output-icon">${icon(output.icon)}</span><span class="output-copy"><strong>${escapeHtml(output.name)}</strong><small>${escapeHtml(output.status)} · ${escapeHtml(output.meta)}</small></span></article>`).join("") || `<p>当前工作尚未生成产出。</p>`}</div>`;
  }

  function renderSessionAgents(session) {
    const agents = config.agentRuns.filter(agent => agent.sessionId === session.id);
    $("#inspectorContent").innerHTML = `<div class="inspector-section-heading"><div><h3>参与智能体</h3><p>${agents.length} 个实例关联当前工作</p></div></div><div class="session-agent-list">${agents.map(agent => `<article class="session-agent-item"><span class="session-agent-icon">${icon(agent.icon)}</span><span class="session-agent-copy"><strong>${escapeHtml(agent.name)}</strong><small>${escapeHtml(agent.status)} · ${escapeHtml(agent.activity)}</small></span></article>`).join("") || `<p>协作者将在工作开始后显示。</p>`}</div>`;
  }

  function renderAgentMonitor(filter = state.agentFilter) {
    const agents = config.agentRuns.filter(agent => filter === "all" || filter === "attention" && agent.attention || filter === "running" && ["运行中", "会议中"].includes(agent.status));
    $("#agentCardTrack").innerHTML = agents.map(agent => `<button class="agent-run-card ${agent.attention ? "attention" : ""}" type="button" data-agent-session="${agent.sessionId}" role="listitem"><span class="agent-run-header"><span class="agent-run-icon">${icon(agent.icon)}</span><span class="agent-run-copy"><strong>${escapeHtml(agent.name)}</strong><small>${escapeHtml(agent.project)} · ${escapeHtml(agent.task)}</small></span><span class="agent-state">${escapeHtml(agent.status)}</span></span><span class="agent-activity">${escapeHtml(agent.activity)}</span><span class="agent-run-progress"><span><i style="width:${agent.progress}%"></i></span><small>${agent.progress}% · ${escapeHtml(agent.eta)}</small></span></button>`).join("");
    $$('[data-agent-filter]').forEach(button => button.setAttribute("aria-pressed", String(button.dataset.agentFilter === filter)));
    refreshIcons();
  }

  function beginBusinessAction(action) {
    pendingAction = action;
    $("#newTaskGoal").value = action.prompt;
    const modeInput = $(`[name="newMode"][value="${action.mode}"]`);
    if (modeInput) modeInput.checked = true;
    const projectInput = $(`[name="newProject"][value="${activeProject().id}"]`);
    if (projectInput) projectInput.checked = true;
    openDialog($("#actionDialog"));
  }

  function findAction(reference) {
    if (!reference.includes(":")) return config.quickActions.find(action => action.id === reference) || config.modules.flatMap(module => module.actions).find(action => action.id === reference);
    const [moduleId, actionId] = reference.split(":");
    return config.modules.find(module => module.id === moduleId)?.actions.find(action => action.id === actionId);
  }

  function createWorkSession(goal, projectId, mode) {
    const project = config.projects.find(item => item.id === projectId) || config.projects[0];
    const base = config.workSessions.find(session => session.projectId === project.id) || config.workSessions[0];
    const id = `new-${Date.now()}`;
    const modeInfo = modeMeta(mode);
    const session = {
      ...clone(base), id, projectId: project.id, mode, conversationKey: mode,
      title: goal.length > 34 ? `${goal.slice(0, 34)}…` : goal,
      shortTitle: pendingAction?.label || (goal.length > 14 ? `${goal.slice(0, 14)}…` : goal),
      objective: goal, status: "准备中", progress: 0, unread: 0,
      taskId: `TASK-NEW-${String(Date.now()).slice(-5)}`, agentCount: mode === "meeting" ? 4 : 1,
      updated: "刚刚", outputs: []
    };
    sessions.set(id, session);
    messagesBySession.set(id, [
      { role: "user", author: "你", time: currentTime(), content: goal },
      { role: "assistant", author: mode === "meeting" ? "主持人" : mode === "expert" ? "行业专家" : "任务协调器", time: currentTime(), content: `已在“${project.name}”项目中创建${modeInfo.label}工作页。我会读取项目文件，只询问缺失信息，然后继续处理。`, change: `工作页已创建 · 项目目录已挂载 · ${project.path}` }
    ]);
    state.openSessionIds.push(id);
    pendingAction = null;
    renderAgentMonitor();
    switchWorkTab(id);
  }

  function sendMessage() {
    const session = activeSession();
    const input = $("#messageInput");
    const content = input.value.trim();
    if (!session || !content) return;
    const messages = messagesBySession.get(session.id) || [];
    messages.push({ role: "user", author: "你", time: currentTime(), content });
    messagesBySession.set(session.id, messages);
    state.drafts[session.id] = "";
    input.value = "";
    renderConversation(session);
    showTyping();
    window.clearTimeout(typingTimer);
    typingTimer = window.setTimeout(() => {
      hideTyping();
      const response = buildResponse(session, content);
      messages.push(response);
      messagesBySession.set(session.id, messages);
      renderConversation(session);
      renderInspector(session);
      toast("当前工作已更新", response.change || "对话已保存到工作记录");
    }, 620);
  }

  function buildResponse(session, content) {
    if (session.mode === "work") {
      const change = updatePlanFromConversation(content);
      let reply = "我已将你的要求放入当前工作上下文，并检查了项目文件、业务步骤和预期产出。";
      if (change.kind === "add") reply = "已增加“补充德语合规页面”，它会在内容校对后执行。新的页面文件将写入项目产出目录。";
      if (change.kind === "skip") reply = `已跳过“${change.step}”，后续步骤和产出依赖已重新检查。`;
      if (change.kind === "replan") reply = "后续步骤已重新编制，已完成文件保持不变，新的执行顺序已经同步。";
      return { role: "assistant", author: "任务协调器", time: currentTime(), content: reply, change: change.label };
    }
    if (session.mode === "meeting") return { role: "assistant", author: "主持人", time: currentTime(), content: "你的意见已加入当前议题。我会让相关角色回应，并把确定事项写入会议纪要和行动项文件。", change: "会议纪要已更新 · 项目产出已同步" };
    return { role: "assistant", author: "行业专家", time: currentTime(), content: "我会继续结合当前项目中的结算、订单和规则文件分析，并把事实、推断和建议分开呈现。", change: "已引用 2 个项目文件 · 分析继续" };
  }

  function updatePlanFromConversation(content) {
    const normalized = content.replace(/\s+/g, "");
    if (/增加.*德语.*合规|德语合规页面/.test(normalized)) {
      if (!task.steps.some(step => step.id === "s-de-legal")) {
        const index = task.steps.findIndex(step => step.id === "s4");
        task.steps.splice(index < 0 ? task.steps.length - 1 : index, 0, { id: "s-de-legal", label: "补充德语合规页面", detail: "产出：documents/德语合规页面.md", status: "queued", isNew: true });
      }
      return { kind: "add", label: "步骤与产出已同步 · 新增 1 个文件" };
    }
    if (/跳过/.test(normalized)) {
      const target = task.steps.find(step => step.status === "queued" && (/校对/.test(normalized) ? step.label.includes("校对") : true));
      if (target) { target.status = "skipped"; target.detail = "由用户在工作对话中跳过"; }
      return { kind: "skip", step: target?.label || "下一待执行步骤", label: "执行计划已同步 · 1 个步骤已跳过" };
    }
    if (/重新编制|修改方案|重做方案|顺序/.test(normalized)) {
      task.steps.filter(step => ["queued", "approval"].includes(step.status)).forEach(step => { step.detail = step.status === "approval" ? "重新编制后仍需用户确认" : "已按新方案重新排队"; });
      return { kind: "replan", label: "执行计划已同步 · 已完成文件保留" };
    }
    return { kind: "none", label: "要求已加入当前工作 · 文件和步骤暂未改变" };
  }

  function showTyping() {
    const stream = $("#messageStream");
    stream.insertAdjacentHTML("beforeend", `<article class="message-group typing-message"><span class="message-avatar">AI</span><div class="message-body"><div class="message-meta"><strong>正在处理</strong></div><div class="message-bubble typing-indicator"><span></span><span></span><span></span></div></div></article>`);
    stream.scrollTop = stream.scrollHeight;
  }
  function hideTyping() { $(".typing-message")?.remove(); }
  function currentTime() { return new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date()); }
  function updateClock() { const clock = $("#monitorClock"); if (clock) clock.textContent = new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }).format(new Date()); }
  function setMonitorExpanded(expanded) {
    state.monitorExpanded = expanded;
    $("#appShell").classList.toggle("monitor-open", expanded);
    $("#agentMonitorToggle").setAttribute("aria-expanded", String(expanded));
    saveState();
  }

  function renderProjectChoice() {
    $("#projectChoice").insertAdjacentHTML("beforeend", config.projects.map((project, index) => `<label><input type="radio" name="newProject" value="${project.id}" ${index === 0 ? "checked" : ""}><span>${icon(project.icon)}<strong>${escapeHtml(project.name)}</strong><small>${project.activeTasks} 项工作 · ${project.activeAgents} 个智能体</small></span></label>`).join(""));
  }

  function renderSecondaryViews() {
    renderProjectsView();
    renderTasksView();
    renderAssetsView();
    renderConversationHistoryView();
    renderAppsView();
    renderExecutionView();
    renderSettingsView();
  }

  function pageHeader(id, title, description, action = "") { return `<header class="page-header"><div><p class="eyebrow">${escapeHtml(config.context.workspace)}</p><h1 id="${id}">${escapeHtml(title)}</h1><p>${escapeHtml(description)}</p></div>${action ? `<div class="page-actions">${action}</div>` : ""}</header>`; }
  function entityCard(item) { return `<article class="entity-card"><header class="entity-card-header"><span class="entity-card-icon">${icon(item.icon)}</span><button class="icon-button" type="button" title="更多操作" aria-label="更多操作">${icon("ellipsis")}</button></header><h3>${escapeHtml(item.title)}</h3><p>${escapeHtml(item.detail)}</p><footer class="entity-card-footer"><span>${escapeHtml(item.footer)}</span><strong>${escapeHtml(item.status)}</strong></footer></article>`; }

  function renderProjectsView() {
    $("#view-projects").innerHTML = `<div class="generic-view">${pageHeader("projectsTitle", "经营项目", "项目并行存在，每个项目拥有独立本地目录、任务集合和智能体运行状态。", `<button class="primary-button" type="button" data-open-new-work>${icon("plus")}新建项目工作</button>`)}<div class="project-grid">${config.projects.map(project => entityCard({ title: project.name, icon: project.icon, detail: `${project.path}\n${project.files} 个文件 · ${project.storage}`, footer: `${project.activeTasks} 项工作 · ${project.activeAgents} 个智能体`, status: project.status })).join("")}</div></div>`;
  }

  function renderTasksView() {
    $("#view-tasks").innerHTML = `<div class="generic-view">${pageHeader("tasksTitle", "业务任务", "任务以独立工作页打开，不会覆盖其他项目和工作的界面状态。", `<button class="primary-button" type="button" data-open-new-work>${icon("plus")}新建工作</button>`)}<div class="data-list">${config.workSessions.map(session => `<article class="data-row"><span class="data-row-main"><strong>${escapeHtml(session.title)}</strong><small>${escapeHtml(projectOf(session).name)} · ${escapeHtml(modeMeta(session.mode).label)}模式</small></span><span>${escapeHtml(session.objective)}</span><span>${session.progress}%</span><span>${escapeHtml(session.status)}</span><button class="icon-button" type="button" data-open-session="${session.id}" title="进入工作" aria-label="进入${escapeHtml(session.shortTitle)}">${icon("arrow-right")}</button></article>`).join("")}</div></div>`;
  }

  function renderAssetsView() {
    const cards = config.projects.flatMap(project => [
      { title: `${project.name} · 文档`, icon: "folder", detail: `${project.path}\\documents`, footer: `${Math.max(5, Math.round(project.files * .08))} 个文件`, status: "本地目录" },
      { title: `${project.name} · 资源与产出`, icon: "folder-output", detail: `${project.path}\\resources / outputs`, footer: `${project.storage}`, status: "项目资源" }
    ]);
    $("#view-assets").innerHTML = `<div class="generic-view">${pageHeader("assetsTitle", "项目文件与业务资产", "统一查看各项目的文档、原始资源、任务输入和工作产出。") }<div class="asset-grid">${cards.map(entityCard).join("")}</div></div>`;
  }

  function renderConversationHistoryView() {
    $("#view-conversations").innerHTML = `<div class="generic-view">${pageHeader("conversationsTitle", "工作对话记录", "对话属于具体工作；打开历史记录会恢复对应项目、文件、步骤和产出上下文。") }<div class="history-list">${config.workSessions.map(session => `<button class="history-item" type="button" data-open-session="${session.id}"><span>${icon(modeMeta(session.mode).icon)}</span><span class="history-copy"><strong>${escapeHtml(session.title)}</strong><small>${escapeHtml(projectOf(session).name)} · ${escapeHtml(modeMeta(session.mode).label)}模式 · 已文件化保存</small></span><small>${escapeHtml(session.updated)}</small></button>`).join("")}</div></div>`;
  }

  function renderAppsView() {
    const apps = [
      { title: "Shopify", icon: "shopping-bag", detail: "德国独立站 · 商品、订单、客户", footer: "8 分钟前同步", status: "已连接" },
      { title: "Amazon DE", icon: "store", detail: "德国站 · 商品、订单、结算", footer: "12 分钟前同步", status: "已连接" },
      { title: "Cloudflare", icon: "cloud-cog", detail: "域名、DNS 与 CDN", footer: "状态正常", status: "已连接" },
      { title: "Instagram", icon: "instagram", detail: "品牌主页与内容发布", footer: "需要重新授权", status: "需处理" },
      { title: "DHL", icon: "truck", detail: "标签、轨迹与履约异常", footer: "5 分钟前同步", status: "已连接" }
    ];
    $("#view-apps").innerHTML = `<div class="generic-view">${pageHeader("appsTitle", "应用与连接", "管理跨项目共享或项目专属的外部业务连接。") }<div class="connection-grid">${apps.map(entityCard).join("")}</div></div>`;
  }

  function renderExecutionView() {
    $("#view-execution").innerHTML = `<div class="generic-view">${pageHeader("executionTitle", "执行中心", "查看完整任务、智能体、审批、异常和资源；底部监控栏负责日常跨项目观察。") }<div class="execution-notice">${icon("info")}<span>底部智能体监控栏适合持续观察；执行中心提供更完整的运行、事件和恢复信息。</span></div><section class="execution-summary"><div><small>后台任务</small><strong>6</strong></div><div><small>智能体实例</small><strong>8</strong></div><div><small>等待处理</small><strong>2</strong></div><div><small>本地产出</small><strong>23</strong></div></section><div class="data-list">${config.agentRuns.map(agent => `<article class="data-row"><span class="data-row-main"><strong>${escapeHtml(agent.name)}</strong><small>${escapeHtml(agent.id)} · ${escapeHtml(agent.project)}</small></span><span>${escapeHtml(agent.activity)}</span><span>${agent.progress}%</span><span>${escapeHtml(agent.status)}</span><button class="icon-button" type="button" data-open-session="${agent.sessionId}" title="进入工作" aria-label="进入对应工作">${icon("arrow-right")}</button></article>`).join("")}</div></div>`;
  }

  function renderSettingsView() {
    const modules = config.modules.map(module => `<div class="setting-row"><span class="setting-copy"><strong>${escapeHtml(module.title)}</strong><small>${escapeHtml(module.summary)}</small></span><label class="switch"><input type="checkbox" data-module-toggle="${module.id}" ${state.hiddenModules.includes(module.id) ? "" : "checked"}><span></span><span class="sr-only">显示${escapeHtml(module.title)}</span></label></div>`).join("");
    $("#view-settings").innerHTML = `<div class="generic-view">${pageHeader("settingsTitle", "设置", "调整工作台、项目文件、运行监控、审批和对话存储。")}<div class="settings-layout"><nav class="settings-nav" aria-label="设置分类"><button type="button" aria-pressed="true" data-settings-tab="workbench">${icon("layout-dashboard")}工作台</button><button type="button" aria-pressed="false" data-settings-tab="projects">${icon("folder-root")}项目文件</button><button type="button" aria-pressed="false" data-settings-tab="storage">${icon("database")}对话与存储</button></nav><div>
      <section class="settings-section is-active" data-settings-section="workbench"><div class="setting-group"><header class="setting-group-header"><h2>行业功能模块</h2><p>只影响全部项目工作台，不改变已打开工作页。</p></header>${modules}</div><div class="setting-group"><header class="setting-group-header"><h2>初始化</h2><p>重新选择经营模式、市场、工作内容和工作习惯。</p></header><div class="setting-row"><span class="setting-copy"><strong>首次初始化配置</strong><small>当前：独立站品牌 · 欧盟</small></span><button class="secondary-button" type="button" id="restartOnboarding">重新运行</button></div></div></section>
      <section class="settings-section" data-settings-section="projects"><div class="setting-group"><header class="setting-group-header"><h2>本地项目目录</h2><p>每个项目独立配置根目录，任务输入和产出使用相对路径。</p></header>${config.projects.map(project => `<div class="setting-row"><span class="setting-copy"><strong>${escapeHtml(project.name)}</strong><small class="storage-path">${escapeHtml(project.path)}</small></span><button class="secondary-button" type="button">更改</button></div>`).join("")}</div></section>
      <section class="settings-section" data-settings-section="storage"><div class="setting-group"><header class="setting-group-header"><h2>对话与产出存储</h2><p>对话正文和工作产出分别落盘，并通过 SQLite 建立索引。</p></header><div class="setting-row"><span class="setting-copy"><strong>SQLite 元数据索引</strong><small>项目、工作、消息、文件和产出定位</small></span><span>正常</span></div><div class="setting-row"><span class="setting-copy"><strong>JSONL 对话正文</strong><small class="storage-path">.cortana/conversations/{expert|work|meeting}/</small></span><span>已同步</span></div><div class="setting-row"><span class="setting-copy"><strong>项目产出目录</strong><small class="storage-path">{ProjectRoot}/outputs/{workId}/</small></span><span>按工作隔离</span></div></div></section>
    </div></div></div>`;
  }

  function renderOnboarding() {
    const steps = config.onboarding.steps;
    $("#onboardingProgress").innerHTML = [...steps, { title: "生成工作台" }].map((step, index) => `<button type="button" data-onboarding-step="${index}" class="${index === onboardingStep ? "is-active" : ""} ${index < onboardingStep ? "is-complete" : ""}"><span>${index < onboardingStep ? "✓" : index + 1}</span><span>${escapeHtml(step.title)}</span></button>`).join("");
    const content = $("#onboardingContent");
    if (onboardingStep < steps.length) {
      const step = steps[onboardingStep];
      const inputType = step.multiple ? "checkbox" : "radio";
      content.innerHTML = `<h2>${escapeHtml(step.title)}</h2><p>${escapeHtml(step.description)}</p><div class="option-grid">${step.options.map((option, index) => { const saved = state.onboardingAnswers[step.id] || []; const checked = saved.includes(option) || (!saved.length && index === 0); return `<label class="option-card"><input type="${inputType}" name="onboarding-${step.id}" value="${escapeHtml(option)}" ${checked ? "checked" : ""}><span>${escapeHtml(option)}</span></label>`; }).join("")}</div>`;
      $("#onboardingNextButton").textContent = "下一步";
    } else {
      content.innerHTML = `<h2>多项目工作台已准备好</h2><p>项目、工作页、本地文件和智能体监控将分别保存状态。</p><div class="onboarding-result"><div>${icon("briefcase-business")}项目并行，不依赖全局当前项目</div><div>${icon("panel-top")}每项工作使用独立工作页</div><div>${icon("folder-tree")}项目文件、资源和产出可见</div><div>${icon("panel-bottom-open")}底部跨项目智能体监控</div></div>`;
      $("#onboardingNextButton").textContent = "进入工作台";
    }
    $("#onboardingBackButton").disabled = onboardingStep === 0;
    refreshIcons();
  }

  function saveOnboardingAnswer() {
    const step = config.onboarding.steps[onboardingStep];
    if (step) state.onboardingAnswers[step.id] = $$(`[name="onboarding-${step.id}"]:checked`).map(input => input.value);
  }
  function nextOnboarding() { saveOnboardingAnswer(); if (onboardingStep < config.onboarding.steps.length) { onboardingStep += 1; renderOnboarding(); } else completeOnboarding("已根据选择生成多项目工作台"); }
  function completeOnboarding(message) { state.onboardingComplete = true; saveState(); closeDialog($("#onboardingDialog")); navigate("workbench"); toast("工作台初始化完成", message); }

  function openDialog(dialog) { if (!dialog || dialog.open) return; if (typeof dialog.showModal === "function") dialog.showModal(); else dialog.setAttribute("open", ""); }
  function closeDialog(dialog) { if (!dialog) return; if (typeof dialog.close === "function" && dialog.open) dialog.close(); else dialog.removeAttribute("open"); }
  function closeMobileNavigation() { $("#appShell").classList.remove("nav-open"); $("#mobileScrim").hidden = true; }

  function toast(title, detail = "") {
    const element = document.createElement("div");
    element.className = "toast";
    element.innerHTML = `${icon("circle-check")}<div><strong>${escapeHtml(title)}</strong>${detail ? `<small>${escapeHtml(detail)}</small>` : ""}</div><button type="button" aria-label="关闭通知">${icon("x")}</button>`;
    element.querySelector("button").addEventListener("click", () => element.remove());
    $("#toastRegion").append(element); refreshIcons(); window.setTimeout(() => element.remove(), 4200);
  }

  function bindEvents() {
    document.addEventListener("click", event => {
      if (event.target.closest("#railExpandToggle")) {
        state.railExpanded = !state.railExpanded;
        applyNavigationState();
        saveState();
      }

      const nav = event.target.closest("[data-nav-target]");
      if (nav) navigate(nav.dataset.navTarget);

      const workTab = event.target.closest("[data-work-tab]");
      if (workTab && !event.target.closest("[data-close-session]")) switchWorkTab(workTab.dataset.workTab);

      const close = event.target.closest("[data-close-session]");
      if (close) { event.stopPropagation(); closeSession(close.dataset.closeSession); }

      const sessionButton = event.target.closest("[data-open-session]");
      if (sessionButton) openSession(sessionButton.dataset.openSession);

      const agentCard = event.target.closest("[data-agent-session]");
      if (agentCard) openSession(agentCard.dataset.agentSession);

      const actionButton = event.target.closest("[data-action-id]");
      if (actionButton) beginBusinessAction(findAction(actionButton.dataset.actionId));

      const moduleAction = event.target.closest("[data-module-action]");
      if (moduleAction) beginBusinessAction(findAction(moduleAction.dataset.moduleAction));

      const projectButton = event.target.closest("[data-project-context]");
      if (projectButton) selectProject(projectButton.dataset.projectContext);

      const contextToast = event.target.closest("[data-context-toast]");
      if (contextToast) toast(contextToast.dataset.contextToast, "已在主工作区打开对应功能");

      const conversationMode = event.target.closest("[data-conversation-mode]");
      if (conversationMode) {
        const active = conversationMode.getAttribute("aria-pressed") === "true";
        $$('[data-conversation-mode]').forEach(button => button.setAttribute("aria-pressed", "false"));
        conversationMode.setAttribute("aria-pressed", String(!active));
        $$('[data-session-mode]', $("#contextSidebarBody")).forEach(item => { item.hidden = !active && item.dataset.sessionMode !== conversationMode.dataset.conversationMode; });
      }

      if (event.target.closest("[data-open-new-work]")) { pendingAction = null; $("#newTaskGoal").value = ""; const projectInput = $(`[name="newProject"][value="${activeProject().id}"]`); if (projectInput) projectInput.checked = true; openDialog($("#actionDialog")); }

      const fileButton = event.target.closest("[data-file-id]");
      if (fileButton) {
        const project = activeProject();
        state.selectedFiles[project.id] = fileButton.dataset.fileId;
        saveState();
        renderResources(project);
        if (activeSession()?.projectId === project.id) renderConversation(activeSession());
        refreshIcons();
      }

      const folderButton = event.target.closest("[data-folder-id]");
      if (folderButton) { const items = folderButton.nextElementSibling; const expanded = folderButton.getAttribute("aria-expanded") === "true"; folderButton.setAttribute("aria-expanded", String(!expanded)); items.hidden = expanded; }

      const resourceFilter = event.target.closest("[data-resource-filter]");
      if (resourceFilter) { state.resourceFilter = resourceFilter.dataset.resourceFilter; saveState(); renderResources(activeProject()); refreshIcons(); }

      if (event.target.closest("[data-use-selected-file]")) useSelectedFile();
      if (event.target.closest("[data-open-selected-file]")) { const project = activeProject(); toast("打开本地文件", findFile(project, state.selectedFiles[project.id])?.name || "文件"); }
      if (event.target.closest("#projectRootButton")) toast("打开项目根目录", activeProject().path);
      if (event.target.closest("#addResourceButton")) toast("加入项目文件", "可选择本地文件并写入当前项目索引");
      if (event.target.closest("#refreshResourcesButton")) toast("项目目录已刷新", "文件索引与本地目录一致");
      if (event.target.closest("[data-open-output-folder]")) toast("打开产出目录", `${projectOf(activeSession()).path}\\outputs\\${activeSession().id}`);
      if (event.target.closest("[data-plan-history]")) toast("计划变更记录", "10:12 由工作对话调整合规顺序");

      const suggestion = event.target.closest("[data-suggestion]");
      if (suggestion) { $("#messageInput").value = suggestion.dataset.suggestion; $("#messageInput").focus(); }

      const inspector = event.target.closest("[data-inspector-tab]");
      if (inspector && activeSession()) { state.inspectorTab = inspector.dataset.inspectorTab; renderInspector(activeSession()); refreshIcons(); }

      const mobilePane = event.target.closest("[data-session-mobile-pane]");
      if (mobilePane) { state.mobileSessionPane = mobilePane.dataset.sessionMobilePane; $(".work-session-layout").dataset.mobilePane = state.mobileSessionPane; $$('[data-session-mobile-pane]').forEach(button => button.setAttribute("aria-selected", String(button === mobilePane))); saveState(); }

      const settingsTab = event.target.closest("[data-settings-tab]");
      if (settingsTab) { $$('[data-settings-tab]').forEach(button => button.setAttribute("aria-pressed", String(button === settingsTab))); $$('[data-settings-section]').forEach(section => section.classList.toggle("is-active", section.dataset.settingsSection === settingsTab.dataset.settingsTab)); }

      const settingsSide = event.target.closest("[data-settings-side]");
      if (settingsSide) { $$('[data-settings-side]').forEach(button => button.setAttribute("aria-current", String(button === settingsSide ? "page" : "false"))); $(`[data-settings-tab="${settingsSide.dataset.settingsSide}"]`)?.click(); }

      const density = event.target.closest("[data-density]");
      if (density) { state.density = density.dataset.density; $$('[data-density]').forEach(button => button.setAttribute("aria-pressed", String(button === density))); saveState(); renderModules(); }

      const agentFilter = event.target.closest("[data-agent-filter]");
      if (agentFilter) { state.agentFilter = agentFilter.dataset.agentFilter; saveState(); renderAgentMonitor(); if (primaryView() === "execution") renderSecondaryNavigation(); }
    });

    $("#navToggle").addEventListener("click", () => { $("#appShell").classList.toggle("nav-open"); $("#mobileScrim").hidden = !$("#appShell").classList.contains("nav-open"); });
    $("#mobileScrim").addEventListener("click", closeMobileNavigation);
    $("#agentMonitorToggle").addEventListener("click", () => setMonitorExpanded(!state.monitorExpanded));
    $("#agentMonitorCollapse").addEventListener("click", () => setMonitorExpanded(false));

    $("#conversationForm").addEventListener("submit", event => { event.preventDefault(); sendMessage(); $("#attachmentPreview").hidden = true; });
    $("#messageInput").addEventListener("input", event => { const session = activeSession(); if (session) { state.drafts[session.id] = event.target.value; saveState(); } });
    $("#messageInput").addEventListener("keydown", event => { if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); $("#conversationForm").requestSubmit(); } });
    $("#attachButton").addEventListener("click", useSelectedFile);
    $("#assetReferenceButton").addEventListener("click", useSelectedFile);
    $("#promptLibraryButton").addEventListener("click", () => { if (!activeSession()) return; $("#messageInput").value = activeSession().mode === "work" ? "重新编制后续步骤，保留已经完成的文件，并说明对产出目录的影响。" : "请基于当前项目文件说明依据、风险和下一步。"; $("#messageInput").focus(); });

    $("#newTaskButton").addEventListener("click", () => { pendingAction = null; $("#newTaskGoal").value = ""; const projectInput = $(`[name="newProject"][value="${activeProject().id}"]`); if (projectInput) projectInput.checked = true; openDialog($("#actionDialog")); });
    $("#customizeButton").addEventListener("click", () => navigate("settings"));
    $("#themeToggle").addEventListener("click", () => { state.theme = state.theme === "light" ? "dark" : "light"; applyTheme(); saveState(); });
    $("#notificationButton").addEventListener("click", () => toast("2 项等待处理", "新品发布确认 · Reel 封面确认"));
    $("#profileButton").addEventListener("click", () => toast(config.context.workspace, `${config.distribution.productName} · ${config.distribution.version}`));
    $("#openFolderButton").addEventListener("click", () => toast("打开本地项目目录", projectOf(activeSession())?.path || ""));
    $("#pauseSessionButton").addEventListener("click", () => toast("当前工作已暂停", "对话、文件和运行状态已保留"));
    $("#sessionMoreButton").addEventListener("click", () => toast("工作操作", "可复制、归档、取消或打开执行详情"));

    $("#closeActionDialog").addEventListener("click", () => closeDialog($("#actionDialog")));
    $("#cancelActionDialog").addEventListener("click", () => closeDialog($("#actionDialog")));
    $("#actionForm").addEventListener("submit", event => { event.preventDefault(); const form = new FormData(event.currentTarget); const goal = $("#newTaskGoal").value.trim() || "建立新的跨境电商工作"; closeDialog($("#actionDialog")); createWorkSession(goal, form.get("newProject") || config.projects[0].id, form.get("newMode") || "work"); });

    $("#onboardingNextButton").addEventListener("click", nextOnboarding);
    $("#onboardingBackButton").addEventListener("click", () => { saveOnboardingAnswer(); onboardingStep = Math.max(0, onboardingStep - 1); renderOnboarding(); });
    $("#useExampleButton").addEventListener("click", () => completeOnboarding("已载入多项目示例配置"));
    $("#closeOnboardingButton").addEventListener("click", () => closeDialog($("#onboardingDialog")));

    document.addEventListener("change", event => {
      if (event.target.matches("[data-module-toggle]")) { const id = event.target.dataset.moduleToggle; state.hiddenModules = event.target.checked ? state.hiddenModules.filter(item => item !== id) : [...new Set([...state.hiddenModules, id])]; saveState(); renderModules(); toast("工作台已更新", `${event.target.checked ? "显示" : "隐藏"}${config.modules.find(module => module.id === id)?.title || "模块"}`); }
    });
    document.addEventListener("input", event => {
      if (!event.target.matches("[data-context-search]")) return;
      const query = event.target.value.trim().toLowerCase();
      $$("[data-context-search-item]", $("#contextSidebarBody")).forEach(item => { item.hidden = Boolean(query) && !item.textContent.toLowerCase().includes(query); });
    });
    document.addEventListener("click", event => { if (event.target.id === "restartOnboarding") { onboardingStep = 0; renderOnboarding(); openDialog($("#onboardingDialog")); } });

    $("#globalSearch").addEventListener("keydown", event => { if (event.key === "Enter" && event.currentTarget.value.trim()) { event.preventDefault(); const query = event.currentTarget.value.trim(); navigate(/文件|素材|文档|产出/.test(query) ? "assets" : /任务|上线|发布/.test(query) ? "tasks" : "conversations"); toast(`正在查看“${query}”`, "已跨项目定位相关内容"); } });
    document.addEventListener("keydown", event => { if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") { event.preventDefault(); $("#globalSearch").focus(); } if (event.key === "Escape") closeMobileNavigation(); });
  }

  function useSelectedFile() {
    const session = activeSession();
    if (!session) return;
    const project = projectOf(session);
    let file = findFile(project, state.selectedFiles[project.id]);
    if (!file) { const firstFolder = project.folders?.find(folder => folder.items.length); file = firstFolder?.items[0]; if (file) state.selectedFiles[project.id] = file.id; }
    if (!file) { toast("当前项目没有可引用文件"); return; }
    const preview = $("#attachmentPreview");
    preview.hidden = false;
    preview.innerHTML = `${icon(file.icon)}<span>${escapeHtml(file.name)}</span><button type="button" aria-label="移除引用">${icon("x")}</button>`;
    preview.querySelector("button").addEventListener("click", () => { preview.hidden = true; });
    renderConversation(session); preview.hidden = false;
    refreshIcons(); $("#messageInput").focus();
  }

  initialize();
})();
