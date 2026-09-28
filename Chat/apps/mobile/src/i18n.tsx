import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from "react";
import type { Section } from "../../../packages/domain/src";
import { agentText } from "./agent-translations";

export type Language = "zh" | "en";

export interface I18nContextType {
  lang: Language;
  setLang: (lang: Language) => void;
  toggleLang: () => void;
  t: (key: string) => string;
  a: (text: string, values?: Record<string, string | number>) => string;
  titles: Record<Section | "connections", { title: string; subtitle: string }>;
  navLabels: Record<Section, string>;
}

const STORAGE_KEY = "app_language";

function getInitialLanguage(): Language {
  if (typeof window !== "undefined" && window.localStorage) {
    const saved = window.localStorage.getItem(STORAGE_KEY);
    if (saved === "zh" || saved === "en") return saved;
  }
  if (typeof navigator !== "undefined" && navigator.language) {
    return navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en";
  }
  return "zh";
}

const translations: Record<Language, Record<string, string>> = {
  zh: {
    // Header & Status
    "status.standby": "随时待命",
    "status.pickingNext": "正在准备下一个任务…",
    "status.readyReview": "待审核确认",
    "status.needsInput": "等待你的输入",
    "header.notifications": "通知与待审批项",
    "header.menu": "打开会话与菜单",
    "lang.toggle": "EN",
    "lang.label": "中 / EN",

    // Welcome Screen
    "welcome.title": "欢迎使用智能助手工作台",
    "welcome.subtitle": "自主代理 · 代码工作区 · 任务执行",
    "welcome.accessKey": "工作区访问密钥",
    "welcome.placeholder": "在线模式需要密钥，本地模式可留空",
    "welcome.open": "进入工作区",
    "welcome.localNotice": "本地工作区无需密钥，确保本地后端服务运行正常。",

    // Chat Screen
    "chat.heroTitle": "智能助手，让工作更轻松",
    "chat.heroSubtitle": "告诉我你想做什么。我可以规划步骤、编写与修改代码、调用应用工具协助你执行。",
    "chat.prompt1": "探索 Hacker News 热门资讯",
    "chat.prompt2": "提取并总结网页核心内容",
    "chat.prompt3": "创建网页监控跟踪目标",
    "chat.connecting": "正在连接服务…",
    "chat.loading": "正在加载会话…",
    "chat.unavailable": "会话暂不可用",
    "chat.inputPlaceholder": "输入消息或指令（Enter 发送）…",
    "chat.retry": "重试回答",
    "chat.latest": "回到底部最新消息",
    "chat.retrySave": "重试保存会话",
    "chat.retryLoad": "重试加载会话",
    "chat.recentResults": "最新结果",
    "chat.hideRecentResults": "隐藏最新结果",
    "chat.sideChat": "侧边会话",
    "chat.retryMain": "重试主会话",
    "chat.upNext": "待执行队列 · 发送前请保持应用开启",
    "chat.paused": "已暂停的消息",
    "chat.sendQueued": "发送队列中的消息",
    "chat.addDoc": "添加附件文档",
    "chat.done": "完成",
    "chat.noPdf": "在“文档”中导入 PDF 即可在会话中使用。",
    "chat.computer": "计算环境",

    // Common & Workspace
    "workspace.opening": "正在打开工作区…",
    "common.tryAgain": "重试",
    "common.backToApps": "返回应用",
    "common.cancel": "取消",
    "common.save": "保存",
    "common.search": "搜索",
    "common.close": "关闭",
    "common.refresh": "刷新",
    "common.delete": "删除",
    "common.edit": "编辑",

    // Nav
    "nav.chat": "对话",
    "nav.code": "代码",
    "nav.activity": "动态",
    "nav.ideas": "灵感",
    "nav.goals": "目标",
    "nav.apps": "应用",
    "nav.skills": "技能",

    // Section Titles
    "section.code.title": "代码工作台",
    "section.code.subtitle": "文件浏览、在线编辑器、终端执行与多模型配置（无需登录）。",
    "section.activity.title": "任务状态",
    "section.activity.subtitle": "执行规划、实时进度、决策审批与执行结果。",
    "section.ideas.title": "建议灵感",
    "section.ideas.subtitle": "结合当前上下文为你推荐的下一步操作。",
    "section.goals.title": "长期目标",
    "section.goals.subtitle": "长期关注的目标与网页变动监控任务。",
    "section.skills.title": "技能广场",
    "section.skills.subtitle": "NVIDIA 与 OpenClaw 智能体技能集市，即装即用。",
    "section.apps.title": "工具与应用",
    "section.apps.subtitle": "已连接的外部应用、能力插件与记忆上下文。",
    "section.connections.title": "工具与应用",
    "section.connections.subtitle": "已连接的能力与集成。",
    "section.mail.title": "邮件会话",
    "section.mail.subtitle": "支持工作流背后的往来邮件。",
    "section.calendar.title": "日程日历",
    "section.calendar.subtitle": "时间规划与重要事项安排。",
    "section.browser.title": "浏览器",
    "section.browser.subtitle": "智能体托管与交互的浏览会话。",
    "section.files.title": "文档文件",
    "section.files.subtitle": "文档表单、填写副本与文件归档。",

    // Code Workbench Sub-tabs & actions
    "code.tab.files": "文件与代码",
    "code.tab.terminal": "终端命令行",
    "code.tab.assistant": "AI 助手",
    "code.tab.models": "模型提供商",
    "code.tab.diff": "代码比对",
    "code.testConnection": "测试模型连接",
    "code.testSuccess": "连接成功",
    "code.testFailed": "连接失败",
    "code.askAiPlaceholder": "输入代码问题、重构需求或指令...",
    "code.filesTitle": "工作区文件",
    "code.terminalTitle": "终端会话",
    "code.modelsTitle": "模型引擎与 API 配置",
    "code.runCommand": "执行指令",
    "code.newCommand": "输入新命令",
    "code.newFile": "新建文件",
    "code.newFolder": "新建文件夹",
    "code.save": "保存文件",
    "code.saved": "文件已保存",
    "code.parentDir": "返回上级",
    "code.noFiles": "暂无文件，点击新建文件或刷新",
    "code.emptyEditor": "从左侧列表选择一个文件进行查看或编辑",
    "code.startEnvironment": "启动环境",
    "code.stopEnvironment": "停止环境",
    "code.environmentReady": "计算环境正常就绪",
    "code.environmentStopped": "计算环境已停止，点击启动以使用终端和文件操作",
    "code.aiReview": "发送给 AI 审查",
    "code.modelProvider": "模型提供商",
    "code.modelName": "模型名称",
    "code.modelBaseUrl": "API 基础接口地址",
    "code.modelApiKey": "API 密钥 (保存在本地)",
    "code.saveModelConfig": "保存模型设置",
    "code.modelSaved": "模型配置已更新",
    "code.quickCommands": "常用指令",

    // Drawer Menu
    "menu.mainChat": "主会话",
    "menu.savedLocal": "保存在本地工作区",
    "menu.delegate": "派发任务",
    "menu.delegateDesc": "制定计划、处理文档或任务汇总",
    "menu.computer": "计算环境",
    "menu.computerDesc": "浏览器与命令行执行环境",
    "menu.settings": "工作区设置",
    "menu.newChat": "开启新会话",
    "menu.sideChats": "侧边会话",
    "menu.codeWorkbench": "代码工作台",
    "menu.codeWorkbenchDesc": "浏览代码、文件编辑与终端控制",
    "menu.skills": "技能广场",
    "menu.skillsDesc": "查看与安装 Bot 专属技能",

    // Settings
    "settings.conversationStorage": "会话存储",
    "settings.localWorkspace": "本地持久化工作区",
    "settings.refresh": "刷新连接",
    "settings.guided": "引导工作流",
    "settings.connected": "已连接模型",
    "settings.notConfigured": "未配置模型",
  },
  en: {
    // Header & Status
    "status.standby": "Here when you need me",
    "status.pickingNext": "Picking up your next task…",
    "status.readyReview": "Ready to review",
    "status.needsInput": "Needs your input",
    "header.notifications": "Notifications and approvals",
    "header.menu": "Open conversations and menu",
    "lang.toggle": "中文",
    "lang.label": "中 / EN",

    // Welcome Screen
    "welcome.title": "Welcome to AI Workbench.",
    "welcome.subtitle": "Autonomous Agents · Code Workspace · Task Execution",
    "welcome.accessKey": "Workspace access key",
    "welcome.placeholder": "Required for a live workspace",
    "welcome.open": "Open workspace",
    "welcome.localNotice": "Local workspaces open without a key. Make sure your local server is running.",

    // Chat Screen
    "chat.heroTitle": "A little help. A lot more room for life.",
    "chat.heroSubtitle": "Tell me what’s on your mind. I can make a plan, edit code, run commands, and work with your apps.",
    "chat.prompt1": "Find cool things on Hacker News",
    "chat.prompt2": "Summarize a website",
    "chat.prompt3": "Keep an eye on a website",
    "chat.connecting": "Connecting…",
    "chat.loading": "Loading conversation…",
    "chat.unavailable": "Conversation unavailable",
    "chat.inputPlaceholder": "Message or instruction (Enter to send)…",
    "chat.retry": "Retry response",
    "chat.latest": "Latest messages",
    "chat.retrySave": "Retry saving conversation",
    "chat.retryLoad": "Retry loading conversation",
    "chat.recentResults": "Recent results",
    "chat.hideRecentResults": "Hide recent results",
    "chat.sideChat": "Side chat",
    "chat.retryMain": "Retry main chat",
    "chat.upNext": "Up next · Keep the app open until sent",
    "chat.paused": "Messages on hold",
    "chat.sendQueued": "Send queued messages",
    "chat.addDoc": "Add a document",
    "chat.done": "Done",
    "chat.noPdf": "Import a PDF in Files to use it in a conversation.",
    "chat.computer": "Computer",

    // Common & Workspace
    "workspace.opening": "Opening your workspace…",
    "common.tryAgain": "Try again",
    "common.backToApps": "Back to Apps",
    "common.cancel": "Cancel",
    "common.save": "Save",
    "common.search": "Search",
    "common.close": "Close",
    "common.refresh": "Refresh",
    "common.delete": "Delete",
    "common.edit": "Edit",

    // Nav
    "nav.chat": "Chat",
    "nav.code": "Code",
    "nav.activity": "Activity",
    "nav.ideas": "Ideas",
    "nav.goals": "Goals",
    "nav.apps": "Apps",
    "nav.skills": "Skills",

    // Section Titles
    "section.code.title": "Code Workbench",
    "section.code.subtitle": "File exploration, code editor, terminal execution and model providers (No login required).",
    "section.activity.title": "Activity",
    "section.activity.subtitle": "Plans, progress, decisions and results.",
    "section.ideas.title": "Ideas",
    "section.ideas.subtitle": "Useful next steps, grounded in your world.",
    "section.goals.title": "Goals",
    "section.goals.subtitle": "Longer-term goals and things to keep an eye on.",
    "section.skills.title": "Skills Marketplace",
    "section.skills.subtitle": "NVIDIA & OpenClaw Agent Skills Marketplace.",
    "section.apps.title": "Apps",
    "section.apps.subtitle": "Connections, capabilities and what your agent remembers.",
    "section.connections.title": "Apps",
    "section.connections.subtitle": "Connections and capabilities.",
    "section.mail.title": "Mail",
    "section.mail.subtitle": "The conversations behind your work.",
    "section.calendar.title": "Calendar",
    "section.calendar.subtitle": "Time for what matters.",
    "section.browser.title": "Browser",
    "section.browser.subtitle": "Your connected browsing sessions.",
    "section.files.title": "Files",
    "section.files.subtitle": "Documents, forms and filled copies.",

    // Code Workbench Sub-tabs & actions
    "code.tab.files": "Files & Code",
    "code.tab.terminal": "Terminal",
    "code.tab.assistant": "AI Assistant",
    "code.tab.models": "Models",
    "code.tab.diff": "Diff",
    "code.testConnection": "Test Connection",
    "code.testSuccess": "Connected successfully",
    "code.testFailed": "Connection failed",
    "code.askAiPlaceholder": "Ask a coding question, refactoring request or instruction...",
    "code.filesTitle": "Workspace Files",
    "code.terminalTitle": "Terminal Session",
    "code.modelsTitle": "Model Engine & API Config",
    "code.runCommand": "Run command",
    "code.newCommand": "Enter new command",
    "code.newFile": "New file",
    "code.newFolder": "New folder",
    "code.save": "Save file",
    "code.saved": "File saved",
    "code.parentDir": "Parent directory",
    "code.noFiles": "No files found. Create a file or refresh.",
    "code.emptyEditor": "Select a file from the list to view or edit",
    "code.startEnvironment": "Start environment",
    "code.stopEnvironment": "Stop environment",
    "code.environmentReady": "Compute environment is ready",
    "code.environmentStopped": "Compute environment is stopped. Click start to run commands or browse files.",
    "code.aiReview": "Send to AI review",
    "code.modelProvider": "Provider",
    "code.modelName": "Model Name",
    "code.modelBaseUrl": "Base URL",
    "code.modelApiKey": "API Key (saved locally)",
    "code.saveModelConfig": "Save model settings",
    "code.modelSaved": "Model configuration updated",
    "code.quickCommands": "Quick commands",

    // Drawer Menu
    "menu.mainChat": "Main chat",
    "menu.savedLocal": "Saved in this workspace",
    "menu.delegate": "Delegate task",
    "menu.delegateDesc": "A plan, document, or spending summary",
    "menu.computer": "Compute environment",
    "menu.computerDesc": "Browser and terminal execution environment",
    "menu.settings": "Workspace settings",
    "menu.newChat": "New chat",
    "menu.sideChats": "Side chats",
    "menu.codeWorkbench": "Code Workbench",
    "menu.codeWorkbenchDesc": "Code exploration, editing and terminal",
    "menu.skills": "Skills Marketplace",
    "menu.skillsDesc": "Browse and manage Bot skills",

    // Settings
    "settings.conversationStorage": "Conversation storage",
    "settings.localWorkspace": "Local persistent workspace",
    "settings.refresh": "Refresh connections",
    "settings.guided": "Guided workflows",
    "settings.connected": "Model connected",
    "settings.notConfigured": "Model not configured",
  },
};

const I18nContext = createContext<I18nContextType | null>(null);

export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Language>(getInitialLanguage);

  const setLang = useCallback((next: Language) => {
    setLangState(next);
    if (typeof window !== "undefined" && window.localStorage) {
      window.localStorage.setItem(STORAGE_KEY, next);
    }
  }, []);

  const toggleLang = useCallback(() => {
    setLang(lang === "zh" ? "en" : "zh");
  }, [lang, setLang]);

  const t = useCallback(
    (key: string): string => {
      return translations[lang][key] || translations.en[key] || key;
    },
    [lang],
  );

  const navLabels: Record<Section, string> = {
    chat: t("nav.chat"),
    code: t("nav.code"),
    activity: t("nav.activity"),
    ideas: t("nav.ideas"),
    goals: t("nav.goals"),
    apps: t("nav.apps"),
    skills: t("nav.skills"),
    today: t("nav.chat"),
    connections: t("nav.apps"),
    mail: t("section.mail.title"),
    calendar: t("section.calendar.title"),
    browser: t("section.browser.title"),
    files: t("section.files.title"),
  };
  const a = useCallback((text: string, values?: Record<string, string | number>) => agentText(lang, text, values), [lang]);

  const titles: Record<Section | "connections", { title: string; subtitle: string }> = {
    chat: { title: t("nav.chat"), subtitle: "" },
    code: {
      title: t("section.code.title"),
      subtitle: t("section.code.subtitle"),
    },
    activity: {
      title: t("section.activity.title"),
      subtitle: t("section.activity.subtitle"),
    },
    ideas: {
      title: t("section.ideas.title"),
      subtitle: t("section.ideas.subtitle"),
    },
    goals: {
      title: t("section.goals.title"),
      subtitle: t("section.goals.subtitle"),
    },
    skills: {
      title: t("section.skills.title"),
      subtitle: t("section.skills.subtitle"),
    },
    apps: {
      title: t("section.apps.title"),
      subtitle: t("section.apps.subtitle"),
    },
    connections: {
      title: t("section.connections.title"),
      subtitle: t("section.connections.subtitle"),
    },
    today: {
      title: t("nav.chat"),
      subtitle: "",
    },
    mail: {
      title: t("section.mail.title"),
      subtitle: t("section.mail.subtitle"),
    },
    calendar: {
      title: t("section.calendar.title"),
      subtitle: t("section.calendar.subtitle"),
    },
    browser: {
      title: t("section.browser.title"),
      subtitle: t("section.browser.subtitle"),
    },
    files: {
      title: t("section.files.title"),
      subtitle: t("section.files.subtitle"),
    },
  };

  return (
    <I18nContext.Provider value={{ lang, setLang, toggleLang, t, a, titles, navLabels }}>
      {children}
    </I18nContext.Provider>
  );
}

export function useI18n() {
  const ctx = useContext(I18nContext);
  if (!ctx) {
    throw new Error("useI18n must be used within I18nProvider");
  }
  return ctx;
}
