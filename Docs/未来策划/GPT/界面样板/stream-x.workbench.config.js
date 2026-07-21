window.StreamXWorkbenchConfig = {
  distribution: {
    id: "stream-x",
    productName: "Stream X",
    productSubtitle: "跨境电商 AI 工作台",
    icon: "assets/brand.png",
    industry: "跨境电商",
    locale: "zh-CN",
    defaultWorkspace: "Northstar Living",
    defaultProject: "德国独立站",
    defaultMode: "work",
    version: "2026.07-preview.4"
  },
  context: {
    workspace: "Northstar Living",
    project: "德国独立站",
    market: "德国 / 欧盟",
    store: "northstarliving.de",
    channels: ["Shopify", "Amazon DE", "Instagram"],
    currency: "EUR"
  },
  projects: [
    {
      id: "germany-site",
      name: "德国独立站",
      type: "独立站项目",
      icon: "store",
      market: "德国 / 欧盟",
      path: "E:\\StreamX\\NorthstarLiving\\Germany-Site",
      status: "经营中",
      progress: 64,
      activeTasks: 2,
      activeAgents: 3,
      files: 428,
      storage: "684 MB",
      updated: "刚刚",
      sessionIds: ["germany-launch", "site-review"],
      folders: [
        { id: "documents", label: "文档", icon: "folder", count: 18, items: [
          { id: "brief", name: "新品上线方案.md", type: "Markdown", size: "18 KB", updated: "10:12", icon: "file-text" },
          { id: "compliance", name: "德国合规清单.xlsx", type: "Excel", size: "46 KB", updated: "昨天", icon: "file-spreadsheet" },
          { id: "brand-guide", name: "品牌规范.pdf", type: "PDF", size: "2.6 MB", updated: "7 月 18 日", icon: "file-text" },
          { id: "checklist", name: "上线检查清单.xlsx", type: "Excel", size: "64 KB", updated: "09:31", icon: "file-spreadsheet" },
          { id: "test-report", name: "支付测试报告.md", type: "Markdown", size: "19 KB", updated: "09:32", icon: "file-text" }
        ]},
        { id: "resources", label: "资源", icon: "folder", count: 248, items: [
          { id: "product-images", name: "Nordic-Glass-Images", type: "文件夹", size: "186 项", updated: "09:48", icon: "images" },
          { id: "product-data", name: "Nordic-Glass-products.xlsx", type: "Excel", size: "326 KB", updated: "09:51", icon: "file-spreadsheet" },
          { id: "copy-source", name: "英文商品文案.docx", type: "Word", size: "84 KB", updated: "昨天", icon: "file-text" }
        ]},
        { id: "inputs", label: "任务输入", icon: "folder-input", count: 4, items: [
          { id: "selected-products", name: "selected-products.json", type: "JSON", size: "12 KB", updated: "10:04", icon: "braces" },
          { id: "market-rules", name: "germany-market-rules.md", type: "Markdown", size: "31 KB", updated: "10:04", icon: "file-text" }
        ]},
        { id: "outputs", label: "产出", icon: "folder-output", count: 9, items: [
          { id: "de-copy", name: "德语商品页草稿.xlsx", type: "Excel", size: "214 KB", updated: "10:24", icon: "file-spreadsheet", output: true },
          { id: "missing-report", name: "缺失字段报告.md", type: "Markdown", size: "8 KB", updated: "10:24", icon: "file-warning", output: true },
          { id: "run-log", name: "任务执行记录.jsonl", type: "JSONL", size: "52 KB", updated: "刚刚", icon: "file-clock", output: true },
          { id: "minutes", name: "德国站上线评审纪要.md", type: "Markdown", size: "14 KB", updated: "刚刚", icon: "notebook-pen", output: true },
          { id: "actions", name: "会议行动项.json", type: "JSON", size: "5 KB", updated: "刚刚", icon: "list-checks", output: true }
        ]}
      ]
    },
    {
      id: "amazon-de",
      name: "Amazon DE",
      type: "平台渠道项目",
      icon: "shopping-bag",
      market: "德国站",
      path: "E:\\StreamX\\NorthstarLiving\\Amazon-DE",
      status: "经营中",
      progress: 38,
      activeTasks: 2,
      activeAgents: 2,
      files: 186,
      storage: "238 MB",
      updated: "4 分钟前",
      sessionIds: ["amazon-reconcile"],
      folders: [
        { id: "documents", label: "文档", icon: "folder", count: 8, items: [
          { id: "settlement-note", name: "7月结算核对说明.md", type: "Markdown", size: "11 KB", updated: "10:06", icon: "file-text" },
          { id: "fee-rules", name: "Amazon-DE-费用规则.pdf", type: "PDF", size: "1.8 MB", updated: "7 月 15 日", icon: "file-text" }
        ]},
        { id: "resources", label: "资源", icon: "folder", count: 24, items: [
          { id: "statement", name: "Settlement-2026-07.csv", type: "CSV", size: "2.4 MB", updated: "09:55", icon: "sheet" },
          { id: "orders", name: "Orders-Refunds.xlsx", type: "Excel", size: "786 KB", updated: "09:56", icon: "file-spreadsheet" }
        ]},
        { id: "outputs", label: "产出", icon: "folder-output", count: 3, items: [
          { id: "difference", name: "结算差异报告.md", type: "Markdown", size: "28 KB", updated: "刚刚", icon: "file-chart-column", output: true },
          { id: "evidence", name: "差异证据.xlsx", type: "Excel", size: "164 KB", updated: "刚刚", icon: "file-spreadsheet", output: true }
        ]}
      ]
    },
    {
      id: "summer-campaign",
      name: "Summer Table 2026",
      type: "营销活动项目",
      icon: "megaphone",
      market: "德国 / 社交渠道",
      path: "E:\\StreamX\\NorthstarLiving\\Campaigns\\Summer-Table-2026",
      status: "执行中",
      progress: 72,
      activeTasks: 2,
      activeAgents: 3,
      files: 96,
      storage: "3.8 GB",
      updated: "8 分钟前",
      sessionIds: ["social-distribution"],
      folders: [
        { id: "documents", label: "文档", icon: "folder", count: 5, items: [
          { id: "content-plan", name: "本周内容排期.xlsx", type: "Excel", size: "38 KB", updated: "09:40", icon: "file-spreadsheet" }
        ]},
        { id: "resources", label: "视频与图片资源", icon: "images", count: 82, items: [
          { id: "reel-01", name: "Reel-Glass-01.mp4", type: "视频", size: "186 MB", updated: "昨天", icon: "file-video" },
          { id: "image-set", name: "Pinterest-Images", type: "文件夹", size: "46 项", updated: "昨天", icon: "images" }
        ]},
        { id: "outputs", label: "产出", icon: "folder-output", count: 14, items: [
          { id: "ig-copy", name: "Instagram发布文案.md", type: "Markdown", size: "22 KB", updated: "刚刚", icon: "file-text", output: true },
          { id: "schedule", name: "渠道发布计划.xlsx", type: "Excel", size: "31 KB", updated: "刚刚", icon: "file-spreadsheet", output: true }
        ]}
      ]
    }
  ],
  workSessions: [
    {
      id: "germany-launch",
      projectId: "germany-site",
      mode: "work",
      conversationKey: "work",
      title: "德国独立站新品上线",
      shortTitle: "德国站新品上线",
      objective: "完成 Nordic Glass 新品德语本地化、合规检查和独立站发布。",
      status: "执行中",
      progress: 46,
      unread: 2,
      taskId: "TASK-240719-017",
      agentCount: 3,
      updated: "刚刚",
      outputs: [
        { name: "德语商品页草稿.xlsx", status: "正在生成", meta: "12 / 18 SKU", icon: "file-spreadsheet" },
        { name: "缺失字段报告.md", status: "已生成", meta: "2 个缺失项", icon: "file-warning" },
        { name: "发布记录", status: "等待发布", meta: "发布后生成", icon: "file-clock" }
      ]
    },
    {
      id: "amazon-reconcile",
      projectId: "amazon-de",
      mode: "expert",
      conversationKey: "amazonExpert",
      title: "Amazon DE 结算差异核对",
      shortTitle: "Amazon 结算核对",
      objective: "核对 7 月账期费用、退款与实际回款差异，并形成处理建议。",
      status: "分析中",
      progress: 31,
      unread: 1,
      taskId: "TASK-240719-022",
      agentCount: 2,
      updated: "4 分钟前",
      outputs: [
        { name: "结算差异报告.md", status: "分析中", meta: "已定位 €286.70", icon: "file-chart-column" },
        { name: "差异证据.xlsx", status: "已生成", meta: "14 条记录", icon: "file-spreadsheet" }
      ]
    },
    {
      id: "site-review",
      projectId: "germany-site",
      mode: "meeting",
      conversationKey: "meeting",
      title: "德国站上线评审",
      shortTitle: "德国站上线评审",
      objective: "由合规、站点、支付和运营角色共同决定上线条件。",
      status: "会议中",
      progress: 58,
      unread: 0,
      taskId: "MEET-240719-006",
      agentCount: 4,
      updated: "刚刚",
      outputs: [
        { name: "上线评审纪要.md", status: "持续记录", meta: "5 条发言", icon: "notebook-pen" },
        { name: "会议行动项.json", status: "已更新", meta: "2 个行动项", icon: "list-checks" }
      ]
    },
    {
      id: "social-distribution",
      projectId: "summer-campaign",
      mode: "work",
      conversationKey: "socialWork",
      title: "Summer Table 社交分发",
      shortTitle: "Summer Table 分发",
      objective: "完成本周 14 条社交内容的审核、排期和渠道发布。",
      status: "等待确认",
      progress: 72,
      unread: 3,
      taskId: "TASK-240719-019",
      agentCount: 3,
      updated: "8 分钟前",
      outputs: [
        { name: "Instagram发布文案.md", status: "待确认", meta: "8 条", icon: "file-text" },
        { name: "渠道发布计划.xlsx", status: "已生成", meta: "14 条排期", icon: "file-spreadsheet" }
      ]
    }
  ],
  agentRuns: [
    { id: "AG-2411", sessionId: "germany-launch", project: "德国独立站", task: "新品上线", name: "内容本地化协作者", status: "运行中", progress: 67, activity: "生成第 13 个 SKU 德语内容", eta: "约 8 分钟", icon: "languages" },
    { id: "AG-2412", sessionId: "germany-launch", project: "德国独立站", task: "新品上线", name: "商品资料检查员", status: "等待输入", progress: 88, activity: "等待 2 个材质字段", eta: "需要处理", icon: "package-search", attention: true },
    { id: "AG-2413", sessionId: "germany-launch", project: "德国独立站", task: "新品上线", name: "合规检查员", status: "排队中", progress: 0, activity: "等待德语内容产出", eta: "预计 10:36", icon: "clipboard-check" },
    { id: "AG-2421", sessionId: "amazon-reconcile", project: "Amazon DE", task: "结算差异核对", name: "结算分析顾问", status: "运行中", progress: 42, activity: "核对退款与平台费用", eta: "约 14 分钟", icon: "scale" },
    { id: "AG-2422", sessionId: "amazon-reconcile", project: "Amazon DE", task: "结算差异核对", name: "订单证据整理员", status: "运行中", progress: 73, activity: "整理 14 条差异证据", eta: "约 5 分钟", icon: "file-search" },
    { id: "AG-2431", sessionId: "social-distribution", project: "Summer Table 2026", task: "社交分发", name: "社交内容编排员", status: "运行中", progress: 79, activity: "编排 Pinterest 发布队列", eta: "约 6 分钟", icon: "send" },
    { id: "AG-2432", sessionId: "social-distribution", project: "Summer Table 2026", task: "社交分发", name: "视频素材处理员", status: "已暂停", progress: 54, activity: "等待 Reel 封面确认", eta: "已暂停", icon: "file-video", attention: true },
    { id: "AG-2441", sessionId: "site-review", project: "德国独立站", task: "上线评审", name: "会议主持人", status: "会议中", progress: 58, activity: "等待支付负责人回应", eta: "2 项待决议", icon: "users-round" }
  ],
  navigation: [
    { id: "workbench", label: "工作台", icon: "layout-dashboard" },
    { id: "projects", label: "经营项目", icon: "briefcase-business", count: 3 },
    { id: "tasks", label: "业务任务", icon: "list-checks", count: 6 },
    { id: "assets", label: "业务资产", icon: "boxes", count: 428 },
    { id: "conversations", label: "对话记录", icon: "messages-square" },
    { id: "apps", label: "应用与连接", icon: "blocks" },
    { id: "execution", label: "执行中心", icon: "activity", advanced: true },
    { id: "settings", label: "设置", icon: "settings" }
  ],
  onboarding: {
    title: "建立你的跨境电商工作台",
    steps: [
      {
        id: "businessModel",
        title: "经营模式",
        description: "用于决定项目、资产和默认任务。",
        multiple: false,
        options: ["独立站品牌", "平台店群", "跨境分销", "单人公司"]
      },
      {
        id: "markets",
        title: "目标市场",
        description: "工作台会准备对应语言、合规和渠道入口。",
        multiple: true,
        options: ["欧盟", "英国", "北美", "东南亚", "中东"]
      },
      {
        id: "workContent",
        title: "主要工作",
        description: "选择日常最常处理的业务内容。",
        multiple: true,
        options: ["服务器与域名", "独立站建设", "商品与内容", "营销与社交", "订单与客服", "履约与结算"]
      },
      {
        id: "workStyle",
        title: "工作习惯",
        description: "只影响自动继续、审批和通知偏好。",
        multiple: false,
        options: ["仅高风险操作确认", "每个阶段都确认", "低风险任务自动继续"]
      }
    ]
  },
  snapshot: [
    { label: "今日订单", value: "184", detail: "+12.4%", tone: "positive", icon: "shopping-bag" },
    { label: "待发布商品", value: "27", detail: "德语 11", tone: "neutral", icon: "package-open" },
    { label: "待处理异常", value: "6", detail: "2 项需确认", tone: "warning", icon: "circle-alert" },
    { label: "本月净回款", value: "€38,624", detail: "预计 €51,900", tone: "neutral", icon: "badge-euro" }
  ],
  quickActions: [
    { id: "create-site", label: "新建独立站", description: "从市场、域名到上线计划", icon: "panel-top", mode: "work", prompt: "为德国市场新建一个独立站任务，先检查现有品牌资料并编制工作步骤。" },
    { id: "import-products", label: "导入商品", description: "清洗、归类并建立商品资产", icon: "package-plus", mode: "work", prompt: "开始导入本周的商品文件，先检查字段和图片完整性。" },
    { id: "translate-products", label: "翻译商品页", description: "翻译并完成本地化校对", icon: "languages", mode: "work", prompt: "把待发布商品翻译为德语，并检查当地表达和合规要求。" },
    { id: "business-diagnosis", label: "经营诊断", description: "分析经营数据与优先事项", icon: "scan-search", mode: "expert", prompt: "请诊断德国独立站最近 7 天转化下降的问题，并给出优先处理建议。" },
    { id: "social-distribution", label: "分发社交内容", description: "编排渠道内容与发布时间", icon: "send", mode: "work", prompt: "基于本周新品建立 Instagram 和 Pinterest 的内容分发任务。" },
    { id: "site-review", label: "站点评审", description: "多角色评审站点上线条件", icon: "users-round", mode: "meeting", prompt: "发起德国独立站上线评审，重点讨论合规、支付和移动端体验。" }
  ],
  modules: [
    {
      id: "infrastructure",
      title: "服务器与域名",
      subtitle: "基础设施与可用性",
      icon: "server-cog",
      summary: "2 台服务器 · 4 个域名 · 1 项待续费",
      actions: [
        { id: "buy-server", label: "购买服务器", icon: "server", mode: "work", prompt: "为德国站评估并购买一台欧洲节点服务器，先列出规格与费用方案。" },
        { id: "register-domain", label: "注册域名", icon: "globe-2", mode: "work", prompt: "为新品牌筛选并注册域名，先进行可用性和商标风险检查。" },
        { id: "dns-check", label: "检查 DNS", icon: "network", mode: "expert", prompt: "检查 northstarliving.de 的 DNS、邮件验证和 CDN 配置是否完整。" }
      ],
      records: [
        { name: "northstarliving.de", meta: "Cloudflare · 正常", status: "正常" },
        { name: "Frankfurt Production", meta: "Hetzner · 23% 负载", status: "运行中" }
      ]
    },
    {
      id: "store",
      title: "独立站与渠道",
      subtitle: "站点、店铺与销售连接",
      icon: "store",
      summary: "1 个独立站 · 2 个平台渠道",
      actions: [
        { id: "create-site-module", label: "建设独立站", icon: "panel-top-open", mode: "work", prompt: "编制德国独立站建设任务，复用当前品牌和商品资产。" },
        { id: "connect-channel", label: "连接销售渠道", icon: "unplug", mode: "work", prompt: "连接新的销售渠道，并检查商品、库存和订单字段映射。" },
        { id: "review-store", label: "评审站点", icon: "clipboard-check", mode: "meeting", prompt: "组织一次独立站评审会议，输出问题、决议和行动项。" }
      ],
      records: [
        { name: "德国独立站", meta: "Shopify · 在线", status: "转化 2.84%" },
        { name: "Amazon DE", meta: "86 个在售商品", status: "同步正常" }
      ]
    },
    {
      id: "catalog",
      title: "商品与内容",
      subtitle: "商品主数据与本地化内容",
      icon: "package-search",
      summary: "428 个商品 · 27 个待发布",
      actions: [
        { id: "new-product", label: "新建商品", icon: "package-plus", mode: "work", prompt: "新建商品资料整理任务，检查标题、规格、图片和价格。" },
        { id: "translate-page", label: "翻译商品页", icon: "languages", mode: "work", prompt: "翻译选中的商品页并生成校对清单。" },
        { id: "optimize-title", label: "优化商品标题", icon: "text-cursor-input", mode: "expert", prompt: "分析选中商品在德国市场的搜索词并优化标题。" }
      ],
      records: [
        { name: "Nordic Glass 系列", meta: "18 个 SKU · 德语 72%", status: "处理中" },
        { name: "Linen Home 系列", meta: "34 个 SKU · 内容完整", status: "可发布" }
      ]
    },
    {
      id: "marketing",
      title: "营销与社交分发",
      subtitle: "活动、素材与渠道发布",
      icon: "megaphone",
      summary: "3 个活动 · 本周 14 条内容",
      actions: [
        { id: "create-campaign", label: "新建营销活动", icon: "calendar-plus", mode: "work", prompt: "为夏季新品建立德国市场营销活动和渠道计划。" },
        { id: "distribute-content", label: "分发社交内容", icon: "send", mode: "work", prompt: "将已审核内容分发到 Instagram 和 Pinterest，并在发布前请求确认。" },
        { id: "campaign-review", label: "复盘营销活动", icon: "chart-no-axes-combined", mode: "meeting", prompt: "召开营销活动复盘会议，输出保留项、问题和下周行动。" }
      ],
      records: [
        { name: "Summer Table 2026", meta: "Instagram · Pinterest", status: "执行中" },
        { name: "本周内容队列", meta: "14 条 · 3 条待确认", status: "待处理" }
      ]
    },
    {
      id: "operations",
      title: "订单、客服与履约",
      subtitle: "交易交付与客户问题",
      icon: "truck",
      summary: "184 个今日订单 · 6 个异常",
      actions: [
        { id: "order-exception", label: "处理异常订单", icon: "package-x", mode: "work", prompt: "检查当前 6 个异常订单，按风险和时效编制处理步骤。" },
        { id: "reply-customer", label: "生成客服回复", icon: "message-circle-reply", mode: "expert", prompt: "根据选中订单和往来记录生成德语客服回复，并标出需人工确认的信息。" },
        { id: "check-fulfillment", label: "检查履约", icon: "route", mode: "work", prompt: "检查最近 24 小时履约延迟，并建立需要跟进的任务。" }
      ],
      records: [
        { name: "订单 #DE-10482", meta: "地址校验失败 · €128.40", status: "需确认" },
        { name: "DHL 履约队列", meta: "96 件 · 3 件延迟", status: "注意" }
      ]
    },
    {
      id: "finance",
      title: "结算与经营",
      subtitle: "回款、费用与经营分析",
      icon: "landmark",
      summary: "2 个结算周期 · 1 项差异",
      actions: [
        { id: "reconcile", label: "核对结算差异", icon: "scale", mode: "expert", prompt: "核对 Amazon DE 本期结算差异，整理证据和处理建议。" },
        { id: "profit-report", label: "生成利润报告", icon: "file-chart-column", mode: "work", prompt: "生成德国市场本月利润报告，列出费用、净回款和异常。" },
        { id: "weekly-review", label: "周经营复盘", icon: "users-round", mode: "meeting", prompt: "开始本周经营复盘，邀请市场、商品、履约和财务角色参与。" }
      ],
      records: [
        { name: "Amazon DE 7 月结算", meta: "差异 €286.70", status: "待核对" },
        { name: "德国站经营快照", meta: "更新于 10:24", status: "已生成" }
      ]
    }
  ],
  currentTask: {
    id: "TASK-240719-017",
    title: "德国独立站新品上线",
    objective: "将 Nordic Glass 新品完成德语本地化、合规检查并发布到德国独立站。",
    progress: 46,
    status: "执行中",
    owner: "商品上线协作组",
    due: "今天 18:00",
    artifacts: ["德语商品页草稿", "合规检查清单", "发布记录"],
    steps: [
      { id: "s1", label: "检查商品资料完整性", detail: "18 个 SKU 已检查", status: "completed" },
      { id: "s2", label: "生成德语商品内容", detail: "12 / 18 已完成", status: "running" },
      { id: "s3", label: "校对本地表达与关键词", detail: "等待上一步产物", status: "queued" },
      { id: "s4", label: "检查欧盟商品与站点合规", detail: "预计 24 分钟", status: "queued" },
      { id: "s5", label: "等待发布确认", detail: "外部发布必须确认", status: "approval" },
      { id: "s6", label: "发布并生成记录", detail: "Shopify 德国站", status: "queued" }
    ]
  },
  tasks: [
    { title: "德国独立站新品上线", type: "商品与内容", status: "执行中", progress: 46, updated: "刚刚", mode: "work" },
    { title: "Summer Table 社交分发", type: "营销与社交", status: "等待确认", progress: 72, updated: "8 分钟前", mode: "work" },
    { title: "Amazon DE 结算差异核对", type: "结算与经营", status: "分析中", progress: 31, updated: "12 分钟前", mode: "expert" },
    { title: "德国站上线评审", type: "独立站与渠道", status: "已安排", progress: 10, updated: "今天 15:30", mode: "meeting" }
  ],
  conversations: {
    expert: {
      label: "专家",
      icon: "messages-square",
      title: "德国站经营诊断",
      subtitle: "跨境经营顾问",
      threads: ["德国站经营诊断", "Amazon 结算差异", "域名与邮件诊断"],
      contexts: ["德国独立站", "近 7 天经营数据", "商品目录"],
      suggestions: ["转为工作任务", "继续分析转化漏斗", "对比 Amazon DE"],
      messages: [
        { role: "assistant", author: "跨境经营顾问", time: "10:18", content: "我已读取德国站近 7 天的流量、商品和订单数据。当前转化下降主要集中在移动端结账阶段，而不是商品页访问量。" },
        { role: "assistant", author: "跨境经营顾问", time: "10:19", content: "优先检查支付方式排序、运费展示时机和德语退货说明。需要我把这三项整理成可执行任务吗？", evidence: "引用：德国站经营快照 · Shopify Analytics" },
        { role: "user", author: "你", time: "10:21", content: "先比较一下 Amazon DE，同类商品是否也出现了下降。" },
        { role: "assistant", author: "跨境经营顾问", time: "10:22", content: "Amazon DE 同类商品转化保持稳定，说明问题更可能来自独立站结账体验。我正在对比移动端漏斗和近期主题变更。" }
      ]
    },
    amazonExpert: {
      label: "专家",
      icon: "messages-square",
      title: "Amazon DE 结算差异核对",
      subtitle: "结算分析顾问",
      threads: ["Amazon DE 结算差异核对"],
      contexts: ["TASK-240719-022", "7 月结算数据", "订单与退款"],
      suggestions: ["导出差异证据", "转为追款任务", "继续核对退款"],
      messages: [
        { role: "assistant", author: "结算分析顾问", time: "10:06", content: "我已读取 7 月结算单、订单和退款记录。当前识别到 14 条差异，净差额为 286.70 欧元。" },
        { role: "assistant", author: "结算分析顾问", time: "10:08", content: "主要差异来自 6 笔退款处理费和 3 笔广告费用跨账期扣减。我正在把平台流水号与订单证据逐条关联。", evidence: "引用：Settlement-2026-07.csv · Orders-Refunds.xlsx" },
        { role: "user", author: "你", time: "10:13", content: "先把能够直接申诉的项目单独列出来，不要混入正常跨期费用。" },
        { role: "assistant", author: "结算分析顾问", time: "10:14", content: "已拆分。当前有 5 条、合计 118.40 欧元具备申诉条件，证据表正在生成。", change: "差异分类已更新 · 新增申诉证据产出" }
      ]
    },
    work: {
      label: "工作",
      icon: "workflow",
      title: "德国独立站新品上线",
      subtitle: "工作任务 · 46%",
      threads: ["德国独立站新品上线", "Summer Table 社交分发", "订单异常处理"],
      contexts: ["TASK-240719-017", "Nordic Glass 系列", "德国独立站"],
      suggestions: ["增加德语合规页面", "调整发布顺序", "暂停并保存进度"],
      messages: [
        { role: "assistant", author: "任务协调器", time: "10:04", content: "已建立新品上线任务，共 6 个业务步骤。商品资料检查已完成，正在生成 18 个 SKU 的德语内容。" },
        { role: "user", author: "你", time: "10:11", content: "修改方案，合规检查要放在内容校对之后，并且发布前让我确认。" },
        { role: "assistant", author: "任务协调器", time: "10:12", content: "方案已更新。合规检查调整到内容校对之后，发布确认保留为强制步骤；后续执行队列已重新编制。", change: "计划已同步 · 调整 2 个步骤 · 不影响已完成产物" },
        { role: "assistant", author: "内容本地化协作者", time: "10:24", content: "德语内容已完成 12 个 SKU。发现 2 个商品缺少材质说明，我会继续处理其余商品，缺失项已加入等待清单。" }
      ]
    },
    socialWork: {
      label: "工作",
      icon: "workflow",
      title: "Summer Table 社交分发",
      subtitle: "工作任务 · 72%",
      threads: ["Summer Table 社交分发"],
      contexts: ["TASK-240719-019", "Instagram / Pinterest", "Summer Table 2026"],
      suggestions: ["确认 Pinterest 排期", "替换 Reel 封面", "暂停外部发布"],
      messages: [
        { role: "assistant", author: "任务协调器", time: "09:44", content: "本周 14 条社交内容已完成渠道适配。Pinterest 队列正在编排，Instagram Reel 等待封面确认。" },
        { role: "assistant", author: "社交内容编排员", time: "09:51", content: "Pinterest 的 6 条内容已完成链接、标签和发布时间检查，剩余 2 条正在调整图片比例。" },
        { role: "user", author: "你", time: "09:56", content: "Reel 封面先不要发布，把需要确认的两个版本保留给我对比。" },
        { role: "assistant", author: "任务协调器", time: "09:57", content: "已暂停 Reel 发布，并把两个封面版本写入项目产出目录。Pinterest 队列不受影响，继续执行。", change: "外部发布已暂停 · 新增 2 个待确认产出" }
      ]
    },
    meeting: {
      label: "会议",
      icon: "users-round",
      title: "德国站上线评审",
      subtitle: "4 个角色 · 2 项待决议",
      threads: ["德国站上线评审", "本周经营复盘", "Summer Table 活动复盘"],
      contexts: ["德国独立站", "上线检查清单", "新品上线任务"],
      suggestions: ["询问合规风险", "要求重新讨论支付", "生成行动项"],
      messages: [
        { role: "assistant", author: "主持人", time: "09:36", content: "本次评审聚焦三个议题：欧盟合规、支付与税费展示、移动端上线质量。先请合规顾问说明当前风险。" },
        { role: "participant", author: "合规顾问", time: "09:38", content: "隐私和退货页面已具备，但包装法注册号尚未进入站点页脚。建议在上线前列为阻断项。" },
        { role: "participant", author: "站点负责人", time: "09:40", content: "页脚改动可以在今天完成，不影响商品内容任务。支付测试还缺少一次 Klarna 移动端验证。" },
        { role: "user", author: "你", time: "09:42", content: "把这两项都形成行动任务，完成后再给最终上线结论。" },
        { role: "assistant", author: "主持人", time: "09:43", content: "已记录。会议暂不作出上线决议，待包装法信息和 Klarna 移动端验证完成后继续评审。", change: "已形成 2 个行动项 · 关联到当前项目" }
      ]
    }
  }
};
