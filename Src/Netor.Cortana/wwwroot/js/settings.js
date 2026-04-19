/**
 * settings.js - 设置界面交互逻辑
 */

// ============================================================
// 桥接对象 & 状态
// ============================================================
const bridge = chrome.webview.hostObjects.sync.settingsBridge;

const cache = {
    providers: [],
    providerTypes: [],
    models: [],
    agents: [],
    plugins: [],
    mcpServers: []
};

// ============================================================
// DOM 引用
// ============================================================
const dom = {
    // 选项卡
    tabBtns: document.querySelectorAll('.tab-btn'),
    tabPanes: document.querySelectorAll('.tab-pane'),

    // 厂商
    providerList: document.getElementById('providerList'),
    providerForm: document.getElementById('providerForm'),
    providerFormTitle: document.getElementById('providerFormTitle'),

    // 模型
    modelProviderSelect: document.getElementById('modelProviderSelect'),
    btnAddModel: document.getElementById('btnAddModel'),
    modelList: document.getElementById('modelList'),
    modelForm: document.getElementById('modelForm'),
    modelFormTitle: document.getElementById('modelFormTitle'),

    // 智能体
    agentList: document.getElementById('agentList'),
    agentForm: document.getElementById('agentForm'),
    agentFormTitle: document.getElementById('agentFormTitle'),

    // 参数微调
    tuningAgentSelect: document.getElementById('tuningAgentSelect'),
    tuningForm: document.getElementById('tuningForm'),

    // 工具管理
    toolsAgentSelect: document.getElementById('toolsAgentSelect'),
    tagFilter: document.getElementById('tagFilter'),
    toolsList: document.getElementById('toolsList'),
    toolsActions: document.getElementById('toolsActions'),

    // MCP 服务
    mcpList: document.getElementById('mcpList'),
    mcpForm: document.getElementById('mcpForm'),
    mcpFormTitle: document.getElementById('mcpFormTitle')
};

// ============================================================
// Toast 提示
// ============================================================
function showToast(msg, type) {
    type = type || 'success';
    var old = document.querySelector('.toast');
    if (old) old.remove();

    var el = document.createElement('div');
    el.className = 'toast ' + type;
    el.textContent = msg;
    document.body.appendChild(el);

    requestAnimationFrame(function () {
        el.classList.add('show');
    });

    setTimeout(function () {
        el.classList.remove('show');
        setTimeout(function () { el.remove(); }, 300);
    }, 2000);
}

// ============================================================
// 选项卡切换
// ============================================================
dom.tabBtns.forEach(function (btn) {
    btn.addEventListener('click', function () {
        var tab = btn.dataset.tab;

        dom.tabBtns.forEach(function (b) { b.classList.remove('active'); });
        btn.classList.add('active');

        dom.tabPanes.forEach(function (p) { p.classList.remove('active'); });
        document.getElementById('pane-' + tab).classList.add('active');

        if (tab === 'system') loadSystemSettings();
        if (tab === 'models') refreshModelProviderSelect();
        if (tab === 'agents') loadAgents();
        if (tab === 'mcp') loadMcpServers();
        if (tab === 'tools') refreshToolsPanel();
        if (tab === 'tuning') refreshTuningAgentSelect();
    });
});

// ============================================================
// AI 厂商 CRUD
// ============================================================
function loadProviderTypes() {
    try {
        var json = bridge.GetProviderDriverDefinitions();
        cache.providerTypes = JSON.parse(json);
    } catch (e) {
        cache.providerTypes = [];
    }
    renderProviderTypeOptions();
}

function renderProviderTypeOptions() {
    var select = document.getElementById('pf-type');
    var current = select.value;

    select.innerHTML = '';

    cache.providerTypes.forEach(function (item) {
        var option = document.createElement('option');
        option.value = item.Id;
        option.textContent = item.DisplayName;
        select.appendChild(option);
    });

    if (!cache.providerTypes.length) {
        return;
    }

    var matched = cache.providerTypes.some(function (item) { return item.Id === current; });
    select.value = matched ? current : cache.providerTypes[0].Id;
}

function getProviderTypeDisplayName(providerType) {
    var matched = cache.providerTypes.find(function (item) { return item.Id === providerType; });
    return matched ? matched.DisplayName : providerType;
}

function loadProviders() {
    try {
        var json = bridge.GetProviders();
        cache.providers = JSON.parse(json);
    } catch (e) {
        cache.providers = [];
    }
    renderProviders();
}

function renderProviders() {
    var list = dom.providerList;
    list.innerHTML = '';

    if (cache.providers.length === 0) {
        list.innerHTML = '<div class="empty-hint">暂无厂商，请点击右上角添加</div>';
        return;
    }

    cache.providers.forEach(function (p) {
        var badges = '';
        if (p.IsDefault) badges += '<span class="badge badge-default">默认</span>';
        if (!p.IsEnabled) badges += '<span class="badge badge-disabled">已禁用</span>';
        var providerTypeName = getProviderTypeDisplayName(p.ProviderType);

        var div = document.createElement('div');
        div.className = 'list-item';
        div.innerHTML =
            '<div class="list-item-info">' +
                '<div class="list-item-name">' + escHtml(p.Name) + badges + '</div>' +
                '<div class="list-item-meta">' + escHtml(providerTypeName) + ' · ' + escHtml(p.Url) + '</div>' +
            '</div>' +
            '<div class="list-item-actions">' +
                '<button class="btn-edit" data-id="' + p.Id + '">编辑</button>' +
                '<button class="btn-danger" data-id="' + p.Id + '">删除</button>' +
            '</div>';
        list.appendChild(div);
    });

    list.querySelectorAll('.btn-edit').forEach(function (btn) {
        btn.addEventListener('click', function () { editProvider(btn.dataset.id); });
    });
    list.querySelectorAll('.btn-danger').forEach(function (btn) {
        btn.addEventListener('click', function () { deleteProvider(btn.dataset.id); });
    });
}

function showProviderForm(entity) {
    dom.providerFormTitle.textContent = entity ? '编辑厂商' : '添加厂商';
    document.getElementById('pf-id').value = entity ? entity.Id : '';
    document.getElementById('pf-name').value = entity ? entity.Name : '';
    document.getElementById('pf-url').value = entity ? entity.Url : '';
    document.getElementById('pf-key').value = entity ? entity.Key : '';
    renderProviderTypeOptions();
    document.getElementById('pf-type').value = entity
        ? entity.ProviderType
        : (cache.providerTypes[0] ? cache.providerTypes[0].Id : '');
    document.getElementById('pf-desc').value = entity ? entity.Description : '';
    document.getElementById('pf-enabled').checked = entity ? entity.IsEnabled : true;
    document.getElementById('pf-default').checked = entity ? entity.IsDefault : false;
    dom.providerForm.classList.remove('hidden');
}

function hideProviderForm() {
    dom.providerForm.classList.add('hidden');
}

function editProvider(id) {
    var entity = cache.providers.find(function (p) { return p.Id === id; });
    if (entity) showProviderForm(entity);
}

function saveProvider() {
    var name = document.getElementById('pf-name').value.trim();
    var url = document.getElementById('pf-url').value.trim();
    var key = document.getElementById('pf-key').value.trim();
    if (!name || !url || !key) {
        showToast('请填写必填项', 'error');
        return;
    }

    var id = document.getElementById('pf-id').value;
    var data = {
        Id: id || undefined,
        Name: name,
        Url: url,
        Key: key,
        ProviderType: document.getElementById('pf-type').value,
        Description: document.getElementById('pf-desc').value.trim(),
        IsEnabled: document.getElementById('pf-enabled').checked,
        IsDefault: document.getElementById('pf-default').checked
    };

    var result = JSON.parse(bridge.SaveProvider(JSON.stringify(data)));
    if (result.success) {
        showToast('保存成功');
        hideProviderForm();
        loadProviders();
    } else {
        showToast(result.error || '保存失败', 'error');
    }
}

function deleteProvider(id) {
    if (!confirm('确定删除该厂商及其所有模型？')) return;

    var result = JSON.parse(bridge.DeleteProvider(id));
    if (result.success) {
        showToast('已删除');
        loadProviders();
    } else {
        showToast(result.error || '删除失败', 'error');
    }
}

document.getElementById('btnAddProvider').addEventListener('click', function () { showProviderForm(null); });
document.getElementById('btnCancelProvider').addEventListener('click', hideProviderForm);
document.getElementById('btnSaveProvider').addEventListener('click', saveProvider);

// ============================================================
// AI 模型 CRUD
// ============================================================
function refreshModelProviderSelect() {
    loadProviders();
    var sel = dom.modelProviderSelect;
    sel.innerHTML = '<option value="">— 选择厂商 —</option>';
    cache.providers.forEach(function (p) {
        var opt = document.createElement('option');
        opt.value = p.Id;
        opt.textContent = p.Name;
        sel.appendChild(opt);
    });
    dom.btnAddModel.disabled = true;
    dom.modelList.innerHTML = '<div class="empty-hint">请先选择一个 AI 厂商</div>';
    hideModelForm();
}

dom.modelProviderSelect.addEventListener('change', function () {
    var pid = this.value;
    dom.btnAddModel.disabled = !pid;
    if (pid) {
        loadModels(pid);
    } else {
        dom.modelList.innerHTML = '<div class="empty-hint">请先选择一个 AI 厂商</div>';
    }
    hideModelForm();
});

function loadModels(providerId) {
    try {
        var json = bridge.GetModels(providerId);
        cache.models = JSON.parse(json);
    } catch (e) {
        cache.models = [];
    }
    renderModels();
}

function renderModels() {
    var list = dom.modelList;
    list.innerHTML = '';

    if (cache.models.length === 0) {
        list.innerHTML = '<div class="empty-hint">暂无模型，请点击右上角添加</div>';
        return;
    }

    cache.models.forEach(function (m) {
        var displayName = m.DisplayName || m.Name;
        var badges = '';
        if (m.IsDefault) badges += '<span class="badge badge-default">默认</span>';
        if (!m.IsEnabled) badges += '<span class="badge badge-disabled">已禁用</span>';

        var div = document.createElement('div');
        div.className = 'list-item';
        div.innerHTML =
            '<div class="list-item-info">' +
                '<div class="list-item-name">' + escHtml(displayName) + badges + '</div>' +
                '<div class="list-item-meta">' + escHtml(m.ModelType) + (m.ContextLength ? ' · ' + m.ContextLength + ' tokens' : '') + '</div>' +
            '</div>' +
            '<div class="list-item-actions">' +
                '<button class="btn-edit" data-id="' + m.Id + '">编辑</button>' +
                '<button class="btn-danger" data-id="' + m.Id + '">删除</button>' +
            '</div>';
        list.appendChild(div);
    });

    list.querySelectorAll('.btn-edit').forEach(function (btn) {
        btn.addEventListener('click', function () { editModel(btn.dataset.id); });
    });
    list.querySelectorAll('.btn-danger').forEach(function (btn) {
        btn.addEventListener('click', function () { deleteModel(btn.dataset.id); });
    });
}

function showModelForm(entity) {
    var pid = dom.modelProviderSelect.value;
    dom.modelFormTitle.textContent = entity ? '编辑模型' : '添加模型';
    document.getElementById('mf-id').value = entity ? entity.Id : '';
    document.getElementById('mf-providerId').value = entity ? entity.ProviderId : pid;
    document.getElementById('mf-name').value = entity ? entity.Name : '';
    document.getElementById('mf-displayName').value = entity ? entity.DisplayName : '';
    document.getElementById('mf-modelType').value = entity ? entity.ModelType : 'chat';
    document.getElementById('mf-contextLength').value = entity ? entity.ContextLength : 0;
    document.getElementById('mf-desc').value = entity ? entity.Description : '';
    document.getElementById('mf-enabled').checked = entity ? entity.IsEnabled : true;
    document.getElementById('mf-default').checked = entity ? entity.IsDefault : false;
    dom.modelForm.classList.remove('hidden');
}

function hideModelForm() {
    dom.modelForm.classList.add('hidden');
}

function editModel(id) {
    var entity = cache.models.find(function (m) { return m.Id === id; });
    if (entity) showModelForm(entity);
}

function saveModel() {
    var name = document.getElementById('mf-name').value.trim();
    if (!name) {
        showToast('请填写模型标识', 'error');
        return;
    }

    var id = document.getElementById('mf-id').value;
    var data = {
        Id: id || undefined,
        ProviderId: document.getElementById('mf-providerId').value,
        Name: name,
        DisplayName: document.getElementById('mf-displayName').value.trim(),
        ModelType: document.getElementById('mf-modelType').value,
        ContextLength: parseInt(document.getElementById('mf-contextLength').value) || 0,
        Description: document.getElementById('mf-desc').value.trim(),
        IsEnabled: document.getElementById('mf-enabled').checked,
        IsDefault: document.getElementById('mf-default').checked
    };

    var result = JSON.parse(bridge.SaveModel(JSON.stringify(data)));
    if (result.success) {
        showToast('保存成功');
        hideModelForm();
        loadModels(data.ProviderId);
    } else {
        showToast(result.error || '保存失败', 'error');
    }
}

function deleteModel(id) {
    if (!confirm('确定删除该模型？')) return;

    var pid = dom.modelProviderSelect.value;
    var result = JSON.parse(bridge.DeleteModel(id));
    if (result.success) {
        showToast('已删除');
        loadModels(pid);
    } else {
        showToast(result.error || '删除失败', 'error');
    }
}

document.getElementById('btnAddModel').addEventListener('click', function () { showModelForm(null); });
document.getElementById('btnCancelModel').addEventListener('click', hideModelForm);
document.getElementById('btnSaveModel').addEventListener('click', saveModel);

// ============================================================
// 智能体 CRUD
// ============================================================
function loadAgents() {
    try {
        var json = bridge.GetAgents();
        cache.agents = JSON.parse(json);
    } catch (e) {
        cache.agents = [];
    }
    renderAgents();
}

function renderAgents() {
    var list = dom.agentList;
    list.innerHTML = '';

    if (cache.agents.length === 0) {
        list.innerHTML = '<div class="empty-hint">暂无智能体，请点击右上角添加</div>';
        return;
    }

    cache.agents.forEach(function (a) {
        var badges = '';
        if (a.IsDefault) badges += '<span class="badge badge-default">默认</span>';
        if (!a.IsEnabled) badges += '<span class="badge badge-disabled">已禁用</span>';

        var div = document.createElement('div');
        div.className = 'list-item';
        div.innerHTML =
            '<div class="list-item-info">' +
                '<div class="list-item-name">' + escHtml(a.Name) + badges + '</div>' +
                '<div class="list-item-meta">' + escHtml(a.Description || '无描述') + '</div>' +
            '</div>' +
            '<div class="list-item-actions">' +
                '<button class="btn-edit" data-id="' + a.Id + '">编辑</button>' +
                '<button class="btn-danger" data-id="' + a.Id + '">删除</button>' +
            '</div>';
        list.appendChild(div);
    });

    list.querySelectorAll('.btn-edit').forEach(function (btn) {
        btn.addEventListener('click', function () { editAgent(btn.dataset.id); });
    });
    list.querySelectorAll('.btn-danger').forEach(function (btn) {
        btn.addEventListener('click', function () { deleteAgent(btn.dataset.id); });
    });
}

function showAgentForm(entity) {
    dom.agentFormTitle.textContent = entity ? '编辑智能体' : '添加智能体';
    document.getElementById('af-id').value = entity ? entity.Id : '';
    document.getElementById('af-name').value = entity ? entity.Name : '';
    document.getElementById('af-desc').value = entity ? entity.Description : '';
    document.getElementById('af-instructions').value = entity ? entity.Instructions : '';
    document.getElementById('af-enabled').checked = entity ? entity.IsEnabled : true;
    document.getElementById('af-default').checked = entity ? entity.IsDefault : false;
    dom.agentForm.classList.remove('hidden');
}

function hideAgentForm() {
    dom.agentForm.classList.add('hidden');
}

function editAgent(id) {
    var entity = cache.agents.find(function (a) { return a.Id === id; });
    if (entity) showAgentForm(entity);
}

function saveAgent() {
    var name = document.getElementById('af-name').value.trim();
    if (!name) {
        showToast('请填写智能体名称', 'error');
        return;
    }

    var id = document.getElementById('af-id').value;
    var data = {
        Id: id || undefined,
        Name: name,
        Description: document.getElementById('af-desc').value.trim(),
        Instructions: document.getElementById('af-instructions').value,
        IsEnabled: document.getElementById('af-enabled').checked,
        IsDefault: document.getElementById('af-default').checked
    };

    var result = JSON.parse(bridge.SaveAgent(JSON.stringify(data)));
    if (result.success) {
        showToast('保存成功');
        hideAgentForm();
        loadAgents();
    } else {
        showToast(result.error || '保存失败', 'error');
    }
}

function deleteAgent(id) {
    if (!confirm('确定删除该智能体？')) return;

    var result = JSON.parse(bridge.DeleteAgent(id));
    if (result.success) {
        showToast('已删除');
        loadAgents();
    } else {
        showToast(result.error || '删除失败', 'error');
    }
}

document.getElementById('btnAddAgent').addEventListener('click', function () { showAgentForm(null); });
document.getElementById('btnCancelAgent').addEventListener('click', hideAgentForm);
document.getElementById('btnSaveAgent').addEventListener('click', saveAgent);

// ============================================================
// 参数微调
// ============================================================
function refreshTuningAgentSelect() {
    loadAgents();
    var sel = dom.tuningAgentSelect;
    sel.innerHTML = '<option value="">— 请选择 —</option>';
    cache.agents.forEach(function (a) {
        var opt = document.createElement('option');
        opt.value = a.Id;
        opt.textContent = a.Name;
        sel.appendChild(opt);
    });
    dom.tuningForm.classList.add('hidden');
}

dom.tuningAgentSelect.addEventListener('change', function () {
    var id = this.value;
    if (!id) {
        dom.tuningForm.classList.add('hidden');
        return;
    }
    var agent = cache.agents.find(function (a) { return a.Id === id; });
    if (!agent) return;

    document.getElementById('tf-id').value = agent.Id;
    setSlider('tf-temperature', agent.Temperature);
    setSlider('tf-topP', agent.TopP);
    setSlider('tf-freqPenalty', agent.FrequencyPenalty);
    setSlider('tf-presPenalty', agent.PresencePenalty);
    document.getElementById('tf-maxTokens').value = agent.MaxTokens || 0;
    document.getElementById('tf-maxHistory').value = agent.MaxHistoryMessages || 0;

    dom.tuningForm.classList.remove('hidden');
});

function setSlider(id, value) {
    var slider = document.getElementById(id);
    slider.value = value;
    document.getElementById(id + '-val').textContent = parseFloat(value).toFixed(2);
}

// 滑块实时显示值
['tf-temperature', 'tf-topP', 'tf-freqPenalty', 'tf-presPenalty'].forEach(function (id) {
    var slider = document.getElementById(id);
    slider.addEventListener('input', function () {
        document.getElementById(id + '-val').textContent = parseFloat(this.value).toFixed(2);
    });
});

document.getElementById('btnSaveTuning').addEventListener('click', function () {
    var id = document.getElementById('tf-id').value;
    var agent = cache.agents.find(function (a) { return a.Id === id; });
    if (!agent) return;

    agent.Temperature = parseFloat(document.getElementById('tf-temperature').value);
    agent.TopP = parseFloat(document.getElementById('tf-topP').value);
    agent.FrequencyPenalty = parseFloat(document.getElementById('tf-freqPenalty').value);
    agent.PresencePenalty = parseFloat(document.getElementById('tf-presPenalty').value);
    agent.MaxTokens = parseInt(document.getElementById('tf-maxTokens').value) || 0;
    agent.MaxHistoryMessages = parseInt(document.getElementById('tf-maxHistory').value) || 0;

    var result = JSON.parse(bridge.SaveAgent(JSON.stringify(agent)));
    if (result.success) {
        showToast('参数已保存');
        loadAgents();
    } else {
        showToast(result.error || '保存失败', 'error');
    }
});

// ============================================================
// 工具管理
// ============================================================
var _toolsCurrentTag = '__all__';

function refreshToolsPanel() {
    loadAgents();
    var sel = dom.toolsAgentSelect;
    sel.innerHTML = '<option value="">— 请选择 —</option>';
    cache.agents.forEach(function (a) {
        var opt = document.createElement('option');
        opt.value = a.Id;
        opt.textContent = a.Name;
        sel.appendChild(opt);
    });
    dom.tagFilter.classList.add('hidden');
    dom.toolsList.classList.add('hidden');
    dom.toolsActions.classList.add('hidden');

    // 加载插件列表
    try {
        var json = bridge.GetAvailablePlugins();
        cache.plugins = JSON.parse(json);
    } catch (e) {
        cache.plugins = [];
    }

    // 加载 MCP 工具列表
    try {
        var json2 = bridge.GetAvailableMcpTools();
        cache.mcpTools = JSON.parse(json2);
    } catch (e) {
        cache.mcpTools = [];
    }
}

dom.toolsAgentSelect.addEventListener('change', function () {
    var agentId = this.value;
    if (!agentId) {
        dom.tagFilter.classList.add('hidden');
        dom.toolsList.classList.add('hidden');
        dom.toolsActions.classList.add('hidden');
        return;
    }
    _toolsCurrentTag = '__all__';
    renderTagFilter();
    renderToolsList(agentId);
    dom.tagFilter.classList.remove('hidden');
    dom.toolsList.classList.remove('hidden');
    dom.toolsActions.classList.remove('hidden');
});

function collectAllTags() {
    var tagSet = {};
    cache.plugins.forEach(function (p) {
        (p.tags || []).forEach(function (t) { tagSet[t] = true; });
    });
    return Object.keys(tagSet).sort();
}

function renderTagFilter() {
    var tags = collectAllTags();
    var container = dom.tagFilter;
    container.innerHTML = '<span class="tag-label">标签筛选：</span>';

    var allBtn = document.createElement('button');
    allBtn.className = 'tag-btn' + (_toolsCurrentTag === '__all__' ? ' active' : '');
    allBtn.dataset.tag = '__all__';
    allBtn.textContent = '全部';
    allBtn.addEventListener('click', function () { onTagClick('__all__'); });
    container.appendChild(allBtn);

    tags.forEach(function (tag) {
        var btn = document.createElement('button');
        btn.className = 'tag-btn' + (_toolsCurrentTag === tag ? ' active' : '');
        btn.dataset.tag = tag;
        btn.textContent = tag;
        btn.addEventListener('click', function () { onTagClick(tag); });
        container.appendChild(btn);
    });
}

function onTagClick(tag) {
    _toolsCurrentTag = tag;
    renderTagFilter();
    var agentId = dom.toolsAgentSelect.value;
    if (agentId) renderToolsList(agentId);
}

function renderToolsList(agentId) {
    var enabledPluginIds = [];
    try {
        var json = bridge.GetAgentEnabledPlugins(agentId);
        enabledPluginIds = JSON.parse(json);
    } catch (e) {
        enabledPluginIds = [];
    }

    var enabledMcpIds = [];
    try {
        var json2 = bridge.GetAgentEnabledMcpServers(agentId);
        enabledMcpIds = JSON.parse(json2);
    } catch (e) {
        enabledMcpIds = [];
    }

    var plugins = cache.plugins;
    if (_toolsCurrentTag !== '__all__') {
        plugins = plugins.filter(function (p) {
            return (p.tags || []).indexOf(_toolsCurrentTag) >= 0;
        });
    }

    var mcpTools = cache.mcpTools || [];

    var list = dom.toolsList;
    list.innerHTML = '';

    if (plugins.length === 0 && mcpTools.length === 0) {
        list.innerHTML = '<div class="empty-hint">暂无可用工具' +
            (_toolsCurrentTag !== '__all__' ? '（当前标签：' + escHtml(_toolsCurrentTag) + '）' : '') +
            '</div>';
        return;
    }

    // 渲染插件卡片
    plugins.forEach(function (p) {
        var checked = enabledPluginIds.indexOf(p.id) >= 0;
        var tagsHtml = (p.tags || []).map(function (t) {
            return '<span class="plugin-tag">' + escHtml(t) + '</span>';
        }).join('');

        var toolsHtml = (p.tools || []).map(function (t) {
            return '<div class="tool-item">' +
                '<div class="tool-item-name">' + escHtml(t.name) + '</div>' +
                (t.description ? '<div class="tool-item-desc">' + escHtml(t.description) + '</div>' : '') +
                '</div>';
        }).join('');

        var card = document.createElement('div');
        card.className = 'plugin-card';
        card.innerHTML =
            '<label class="plugin-header">' +
                '<input type="checkbox" class="plugin-check" data-plugin-id="' + escHtml(p.id) + '"' +
                (checked ? ' checked' : '') + ' />' +
                '<span class="plugin-name">' + escHtml(p.name) + '</span>' +
                '<span class="source-badge source-plugin">插件</span>' +
            '</label>' +
            (p.description ? '<p class="plugin-desc">' + escHtml(p.description) + '</p>' : '') +
            (tagsHtml ? '<div class="plugin-tags">' + tagsHtml + '</div>' : '') +
            (toolsHtml ? '<div class="plugin-tools">' + toolsHtml + '</div>' : '');

        list.appendChild(card);
    });

    // 渲染 MCP 工具卡片
    mcpTools.forEach(function (m) {
        var checked = enabledMcpIds.indexOf(m.id) >= 0;

        var toolsHtml = (m.tools || []).map(function (t) {
            return '<div class="tool-item">' +
                '<div class="tool-item-name">' + escHtml(t.name) + '</div>' +
                (t.description ? '<div class="tool-item-desc">' + escHtml(t.description) + '</div>' : '') +
                '</div>';
        }).join('');

        var card = document.createElement('div');
        card.className = 'plugin-card mcp-card';
        card.innerHTML =
            '<label class="plugin-header">' +
                '<input type="checkbox" class="mcp-check" data-mcp-id="' + escHtml(m.id) + '"' +
                (checked ? ' checked' : '') + ' />' +
                '<span class="plugin-name">' + escHtml(m.name) + '</span>' +
                '<span class="source-badge source-mcp">MCP</span>' +
            '</label>' +
            (m.description ? '<p class="plugin-desc">' + escHtml(m.description) + '</p>' : '') +
            (toolsHtml ? '<div class="plugin-tools">' + toolsHtml + '</div>' : '');

        list.appendChild(card);
    });
}

document.getElementById('btnSaveTools').addEventListener('click', function () {
    var agentId = dom.toolsAgentSelect.value;
    if (!agentId) {
        showToast('请先选择智能体', 'error');
        return;
    }

    // 保存插件启用配置
    var pluginChecks = dom.toolsList.querySelectorAll('.plugin-check');
    var enabledPluginIds = [];
    pluginChecks.forEach(function (cb) {
        if (cb.checked) enabledPluginIds.push(cb.dataset.pluginId);
    });

    var result1 = JSON.parse(bridge.SaveAgentPlugins(agentId, JSON.stringify(enabledPluginIds)));
    if (!result1.success) {
        showToast(result1.error || '插件配置保存失败', 'error');
        return;
    }

    // 保存 MCP 启用配置
    var mcpChecks = dom.toolsList.querySelectorAll('.mcp-check');
    var enabledMcpIds = [];
    mcpChecks.forEach(function (cb) {
        if (cb.checked) enabledMcpIds.push(cb.dataset.mcpId);
    });

    var result2 = JSON.parse(bridge.SaveAgentMcpServers(agentId, JSON.stringify(enabledMcpIds)));
    if (result2.success) {
        showToast('工具配置已保存');
    } else {
        showToast(result2.error || 'MCP 配置保存失败', 'error');
    }
});

// ============================================================
// MCP 服务管理
// ============================================================
function loadMcpServers() {
    try {
        var json = bridge.GetMcpServers();
        cache.mcpServers = JSON.parse(json);
    } catch (e) {
        cache.mcpServers = [];
    }
    renderMcpServers();
}

function renderMcpServers() {
    var list = dom.mcpList;
    list.innerHTML = '';

    if (cache.mcpServers.length === 0) {
        list.innerHTML = '<div class="empty-hint">暂无 MCP 服务，请点击右上角添加</div>';
        return;
    }

    cache.mcpServers.forEach(function (m) {
        var badges = '';
        if (!m.IsEnabled) badges += '<span class="badge badge-disabled">已禁用</span>';

        var transportLabel = m.TransportType === 'stdio' ? m.Command : m.Url;

        var div = document.createElement('div');
        div.className = 'list-item';
        div.innerHTML =
            '<div class="list-item-info">' +
                '<div class="list-item-name">' + escHtml(m.Name) + badges +
                    '<span class="badge mcp-transport-badge">' + escHtml(m.TransportType) + '</span>' +
                '</div>' +
                '<div class="list-item-meta">' + escHtml(transportLabel || '未配置') + '</div>' +
            '</div>' +
            '<div class="list-item-actions">' +
                '<button class="btn-edit" data-id="' + m.Id + '">编辑</button>' +
                '<button class="btn-danger" data-id="' + m.Id + '">删除</button>' +
            '</div>';
        list.appendChild(div);
    });

    list.querySelectorAll('.btn-edit').forEach(function (btn) {
        btn.addEventListener('click', function () { editMcpServer(btn.dataset.id); });
    });
    list.querySelectorAll('.btn-danger').forEach(function (btn) {
        btn.addEventListener('click', function () { deleteMcpServer(btn.dataset.id); });
    });
}

function showMcpForm(entity) {
    dom.mcpFormTitle.textContent = entity ? '编辑 MCP 服务' : '添加 MCP 服务';
    document.getElementById('mcpf-id').value = entity ? entity.Id : '';
    document.getElementById('mcpf-name').value = entity ? entity.Name : '';
    document.getElementById('mcpf-transport').value = entity ? entity.TransportType : 'stdio';
    document.getElementById('mcpf-command').value = entity ? entity.Command : '';
    document.getElementById('mcpf-arguments').value = entity ? (entity.Arguments || []).join(',') : '';
    document.getElementById('mcpf-url').value = entity ? entity.Url : '';
    document.getElementById('mcpf-apikey').value = entity ? entity.ApiKey : '';
    document.getElementById('mcpf-desc').value = entity ? entity.Description : '';
    document.getElementById('mcpf-enabled').checked = entity ? entity.IsEnabled : true;

    // 环境变量
    var envText = '';
    if (entity && entity.EnvironmentVariables) {
        Object.keys(entity.EnvironmentVariables).forEach(function (k) {
            envText += k + '=' + entity.EnvironmentVariables[k] + '\n';
        });
    }
    document.getElementById('mcpf-envVars').value = envText.trim();

    toggleMcpTransportFields();
    dom.mcpForm.classList.remove('hidden');
}

function hideMcpForm() {
    dom.mcpForm.classList.add('hidden');
}

function toggleMcpTransportFields() {
    var transport = document.getElementById('mcpf-transport').value;
    var stdioFields = document.getElementById('mcpf-stdio-fields');
    var httpFields = document.getElementById('mcpf-http-fields');
    if (transport === 'stdio') {
        stdioFields.classList.remove('hidden');
        httpFields.classList.add('hidden');
    } else {
        stdioFields.classList.add('hidden');
        httpFields.classList.remove('hidden');
    }
}

document.getElementById('mcpf-transport').addEventListener('change', toggleMcpTransportFields);

function editMcpServer(id) {
    var entity = cache.mcpServers.find(function (m) { return m.Id === id; });
    if (entity) showMcpForm(entity);
}

function saveMcpServer() {
    var name = document.getElementById('mcpf-name').value.trim();
    var transport = document.getElementById('mcpf-transport').value;

    if (!name) {
        showToast('请填写名称', 'error');
        return;
    }

    if (transport === 'stdio') {
        var cmd = document.getElementById('mcpf-command').value.trim();
        if (!cmd) {
            showToast('请填写启动命令', 'error');
            return;
        }
    } else {
        var url = document.getElementById('mcpf-url').value.trim();
        if (!url) {
            showToast('请填写服务器地址', 'error');
            return;
        }
    }

    // 解析参数列表
    var argsText = document.getElementById('mcpf-arguments').value.trim();
    var args = argsText ? argsText.split(',').map(function (s) { return s.trim(); }).filter(Boolean) : [];

    // 解析环境变量
    var envText = document.getElementById('mcpf-envVars').value.trim();
    var envVars = {};
    if (envText) {
        envText.split('\n').forEach(function (line) {
            line = line.trim();
            if (!line) return;
            var idx = line.indexOf('=');
            if (idx > 0) {
                envVars[line.substring(0, idx).trim()] = line.substring(idx + 1).trim();
            }
        });
    }

    var id = document.getElementById('mcpf-id').value;
    var data = {
        Id: id || undefined,
        Name: name,
        TransportType: transport,
        Command: document.getElementById('mcpf-command').value.trim(),
        Arguments: args,
        Url: document.getElementById('mcpf-url').value.trim(),
        ApiKey: document.getElementById('mcpf-apikey').value.trim(),
        EnvironmentVariables: envVars,
        Description: document.getElementById('mcpf-desc').value.trim(),
        IsEnabled: document.getElementById('mcpf-enabled').checked
    };

    var result = JSON.parse(bridge.SaveMcpServer(JSON.stringify(data)));
    if (result.success) {
        showToast('保存成功');
        hideMcpForm();
        loadMcpServers();
    } else {
        showToast(result.error || '保存失败', 'error');
    }
}

function deleteMcpServer(id) {
    if (!confirm('确定删除该 MCP 服务？')) return;

    var result = JSON.parse(bridge.DeleteMcpServer(id));
    if (result.success) {
        showToast('已删除');
        loadMcpServers();
    } else {
        showToast(result.error || '删除失败', 'error');
    }
}

function testMcpConnection() {
    var name = document.getElementById('mcpf-name').value.trim();
    var transport = document.getElementById('mcpf-transport').value;

    var data = {
        Name: name || 'test',
        TransportType: transport,
        Command: document.getElementById('mcpf-command').value.trim(),
        Arguments: document.getElementById('mcpf-arguments').value.trim()
            ? document.getElementById('mcpf-arguments').value.trim().split(',').map(function (s) { return s.trim(); })
            : [],
        Url: document.getElementById('mcpf-url').value.trim(),
        ApiKey: document.getElementById('mcpf-apikey').value.trim(),
        EnvironmentVariables: {},
        IsEnabled: true
    };

    // 解析环境变量
    var envText = document.getElementById('mcpf-envVars').value.trim();
    if (envText) {
        envText.split('\n').forEach(function (line) {
            line = line.trim();
            if (!line) return;
            var idx = line.indexOf('=');
            if (idx > 0) {
                data.EnvironmentVariables[line.substring(0, idx).trim()] = line.substring(idx + 1).trim();
            }
        });
    }

    showToast('正在测试连接...');

    try {
        var result = JSON.parse(bridge.TestMcpConnection(JSON.stringify(data)));
        if (result.success) {
            showToast('连接成功，发现 ' + result.toolCount + ' 个工具');
        } else {
            showToast(result.error || '连接失败', 'error');
        }
    } catch (e) {
        showToast('测试失败：' + e.message, 'error');
    }
}

document.getElementById('btnAddMcp').addEventListener('click', function () { showMcpForm(null); });
document.getElementById('btnCancelMcp').addEventListener('click', hideMcpForm);
document.getElementById('btnSaveMcp').addEventListener('click', saveMcpServer);
document.getElementById('btnTestMcp').addEventListener('click', testMcpConnection);

// ============================================================
// 工具函数
// ============================================================
function escHtml(str) {
    if (!str) return '';
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// ============================================================
// 初始化
// ============================================================
loadProviderTypes();
loadProviders();

// ============================================================
// 系统设置
// ============================================================
var _systemSettings = [];

function loadSystemSettings() {
    try {
        var json = bridge.GetSystemSettings();
        _systemSettings = JSON.parse(json);
    } catch (e) {
        _systemSettings = [];
    }
    renderSystemSettings();
}

function renderSystemSettings() {
    var container = document.getElementById('systemSettingsContainer');
    if (!container) return;

    if (_systemSettings.length === 0) {
        container.innerHTML = '<div class="empty-hint">暂无系统设置项</div>';
        return;
    }

    // 按分组聚合
    var groups = {};
    _systemSettings.forEach(function (s) {
        var g = s.Group || '其他';
        if (!groups[g]) groups[g] = [];
        groups[g].push(s);
    });

    var html = '';
    Object.keys(groups).forEach(function (groupName) {
        html += '<div class="form-group"><h4>' + escHtml(groupName) + '</h4>';
        groups[groupName].forEach(function (s) {
            html += renderSystemSettingItem(s);
        });
        html += '</div>';
    });

    container.innerHTML = html;

    // 绑定工作目录选择按钮
    var btnBrowse = document.getElementById('sys-browse-workspace');
    if (btnBrowse) {
        btnBrowse.addEventListener('click', function () {
            try {
                var result = JSON.parse(bridge.SelectWorkspaceDirectory());
                if (result.success && result.path) {
                    var input = document.getElementById('sys-input-System.WorkspaceDirectory');
                    if (input) input.value = result.path;
                }
            } catch (e) {
                showToast('目录选择失败：' + e.message, 'error');
            }
        });
    }

    // 绑定滑块实时显示值
    container.querySelectorAll('input[type="range"]').forEach(function (slider) {
        var valSpan = document.getElementById(slider.id + '-val');
        if (valSpan) {
            slider.addEventListener('input', function () {
                valSpan.textContent = parseFloat(this.value).toFixed(2);
            });
        }
    });
}

function renderSystemSettingItem(s) {
    var key = s.Id;
    var inputId = 'sys-input-' + key;
    var val = s.Value !== undefined && s.Value !== null ? s.Value : s.DefaultValue;
    var hint = s.Description ? '<p class="field-hint">' + escHtml(s.Description) + '</p>' : '';

    // 工作目录：文本框 + 浏览按钮
    if (key === 'System.WorkspaceDirectory') {
        return '<div class="form-row">' +
            '<label>' + escHtml(s.DisplayName) + '</label>' +
            '<div style="display:flex;gap:6px">' +
            '<input type="text" id="' + inputId + '" value="' + escHtml(val) + '" style="flex:1" />' +
            '<button class="btn-secondary btn-sm" id="sys-browse-workspace">📂</button>' +
            '</div>' +
            hint + '</div>';
    }

    // bool 类型：复选框
    if (s.ValueType === 'bool') {
        var checked = val === 'true' || val === '1' ? ' checked' : '';
        return '<div class="form-row row-inline">' +
            '<label class="checkbox-label">' +
            '<input type="checkbox" id="' + inputId + '"' + checked + ' /> ' +
            escHtml(s.DisplayName) +
            '</label>' +
            hint + '</div>';
    }

    // int 类型：数字输入框
    if (s.ValueType === 'int') {
        return '<div class="form-row">' +
            '<label>' + escHtml(s.DisplayName) + '</label>' +
            '<input type="number" id="' + inputId + '" value="' + escHtml(val) + '" step="1" />' +
            hint + '</div>';
    }

    // float 类型：滑块 + 数值显示
    if (s.ValueType === 'float') {
        var fval = parseFloat(val) || 0;
        var isSpeed = key === 'Tts.Speed';
        var min = isSpeed ? '0.5' : '0';
        var max = isSpeed ? '3' : (key.indexOf('Timeout') >= 0 || key.indexOf('Length') >= 0 ? '60' : '30');
        var step = fval < 1 ? '0.01' : '0.5';

        // KWS threshold: 0~1 步进 0.01
        if (key === 'SherpaOnnx.KeywordsThreshold') { min = '0'; max = '1'; step = '0.01'; }
        else if (key === 'SherpaOnnx.KeywordsScore') { min = '1'; max = '20'; step = '0.5'; }
        else if (key === 'SherpaOnnx.NumTrailingBlanks') {
            // int 实际存为 float 键，按整数处理
            return '<div class="form-row">' +
                '<label>' + escHtml(s.DisplayName) + '</label>' +
                '<input type="number" id="' + inputId + '" value="' + escHtml(val) + '" step="1" min="1" max="10" />' +
                hint + '</div>';
        }

        return '<div class="form-row slider-row">' +
            '<label>' + escHtml(s.DisplayName) + '</label>' +
            '<div class="slider-wrap">' +
            '<input type="range" id="' + inputId + '" min="' + min + '" max="' + max + '" step="' + step + '" value="' + fval + '" />' +
            '<span class="slider-val" id="' + inputId + '-val">' + fval.toFixed(2) + '</span>' +
            '</div>' +
            hint + '</div>';
    }

    // 默认：文本框
    return '<div class="form-row">' +
        '<label>' + escHtml(s.DisplayName) + '</label>' +
        '<input type="text" id="' + inputId + '" value="' + escHtml(val) + '" />' +
        hint + '</div>';
}

function collectSystemSettings() {
    return _systemSettings.map(function (s) {
        var inputId = 'sys-input-' + s.Id;
        var el = document.getElementById(inputId);
        var value = s.Value;

        if (el) {
            if (el.type === 'checkbox') {
                value = el.checked ? 'true' : 'false';
            } else {
                value = el.value;
            }
        }

        return { Id: s.Id, Value: value };
    });
}

document.getElementById('btnSaveSystem').addEventListener('click', function () {
    var updates = collectSystemSettings();
    try {
        var result = JSON.parse(bridge.SaveSystemSettings(JSON.stringify(updates)));
        if (result.success) {
            showToast('系统设置已保存');
            loadSystemSettings();
        } else {
            showToast(result.error || '保存失败', 'error');
        }
    } catch (e) {
        showToast('保存失败：' + e.message, 'error');
    }
});

document.getElementById('btnResetSystem').addEventListener('click', function () {
    if (!confirm('确定将所有系统设置恢复为默认值？')) return;
    try {
        var result = JSON.parse(bridge.ResetSystemSettings());
        if (result.success) {
            showToast('已恢复默认设置');
            loadSystemSettings();
        } else {
            showToast(result.error || '重置失败', 'error');
        }
    } catch (e) {
        showToast('重置失败：' + e.message, 'error');
    }
});
