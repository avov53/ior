import { useEffect, useRef, useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { createPortal } from "react-dom";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { brandIcon, flagImage } from "./visualAssets";
import { subscriptionStatus } from "./subscriptionStatus";
import "./styles.css";

type Page = "dashboard" | "profiles" | "routing" | "logs" | "settings";
type Server = { id: number; nativeId?: string; subscriptionId?: string; countryCode?: string; flag: string; name: string; place: string; protocol: string; ping: number; load: number; subscription: string; usedGb: number; totalGb: number; expires: string; kind: "subscription" | "config" };

type NativeSnapshot = {
  operation?: { id: string; status: string; stage: string; result?: unknown; error?: string };
  update?: { status: "notchecked" | "available" | "downloading" | "ready" | "failed"; tag?: string; percent?: number | null; error?: string | null; hasAsset: boolean };
  connected: boolean; connecting: boolean; connectedSeconds: number; selectedServerId?: string; runningServerId?: string | null; runningServerName?: string | null; runtimeConnectionWarning?: string | null;
  tunReadiness?: { ready: boolean; https: {status:string;code:string;durationMs:number}; dns: {status:string;code:string;durationMs:number}; udp: {status:string;code:string;durationMs:number} } | null;
  core?: { selected: string; activeBackend: string | null; buildId?: string | null; protocol?: string | null; mode?: string; options: Array<{ id: string; name: string; selectable: boolean; available: boolean; backend: string | null; detail: string; combinations?: Array<{protocol:string;mode:string;status:string;implementation:string;verified:boolean}> }> };
  activeRoutingProfileId?: string; activeConnectionProfileId?: string;
  availableServerIds?: string[];
  traffic: { download: string; upload: string; ping?: number; quality: string };
  diagnostics?: { appCpuPercent: number | null; engineCpuPercent: number | null; logLinesPerSecond: number; tunDnsLinesPerSecond: number; directPacketLinesPerSecond: number; mode: string; systemProxy: boolean };
  settings: Record<string, unknown>;
  configValidation?: { checkedConfig: boolean; valid?: boolean | null };
  servers: Array<{ id: string; name: string; protocol: string; server: string; port: number; countryCode?: string; countryName?: string; subscriptionId?: string; uploadBytes?: number; downloadBytes?: number; totalBytes?: number; expireUtc?: string; ping?: number }>;
  subscriptions: Array<Record<string, any>>; routes: Array<Record<string, any>>; connectionProfiles: Array<Record<string, any>>;
  services: Array<Record<string, any>>; logs: string[];
};

declare global { interface Window { chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (name: string, listener: (event: MessageEvent) => void) => void } } } }

const nativeBridge = (() => {
  const webview = window.chrome?.webview;
  const pending = new Map<string, { resolve: (value: any) => void; reject: (error: Error) => void }>();
  const listeners = new Set<(snapshot: NativeSnapshot) => void>();
  const operations = new Map<string, { resolve: (value: any) => void; reject: (error: Error) => void }>();
  const finishOperation = (operation: NativeSnapshot["operation"]) => {
    if (!operation || operation.status === "running") return;
    const waiter = operations.get(operation.id);
    if (!waiter) return;
    operations.delete(operation.id);
    operation.status === "succeeded" ? waiter.resolve(operation.result) : waiter.reject(new Error(operation.error || "Операция завершилась с ошибкой"));
  };
  let latest: NativeSnapshot | null = null;
  webview?.addEventListener("message", event => {
    const message = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    if (message?.type === "response" && pending.has(message.id)) {
      const waiter = pending.get(message.id)!; pending.delete(message.id);
      message.ok ? waiter.resolve(message.result) : waiter.reject(new Error(message.error || "Команда завершилась с ошибкой"));
    }
    if (message?.type === "event" && message.name === "state") { latest = { ...latest, ...message.payload, logs: message.payload.logs ?? latest?.logs ?? [] }; finishOperation(latest?.operation); listeners.forEach(listener => listener(latest!)); }
    if (message?.type === "event" && message.name === "logs" && latest) { latest = { ...latest, logs: [...latest.logs, ...message.payload].slice(-500) }; listeners.forEach(listener => listener(latest!)); }
  });
  return {
    available: Boolean(webview), latest: () => latest,
    request<T = unknown>(method: string, payload: unknown = {}): Promise<T> {
      if (!webview) return Promise.reject(new Error("Native bridge unavailable"));
      const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
      return new Promise<T>((resolve, reject) => {
        const timer = window.setTimeout(() => { if (pending.delete(id)) reject(new Error("Превышено время ожидания ответа C#")); }, 15000);
        pending.set(id, { reject: error => { window.clearTimeout(timer); reject(error); }, resolve: value => {
          window.clearTimeout(timer);
          if (value?.accepted && value.operationId) { operations.set(value.operationId, { resolve, reject }); finishOperation(latest?.operation); }
          else resolve(value);
        } });
        webview.postMessage({ type: "request", id, method, payload });
      });
    },
    subscribe(listener: (snapshot: NativeSnapshot) => void) { listeners.add(listener); if (latest) listener(latest); return () => { listeners.delete(listener); }; }
  };
})();

function useNativeSnapshot() {
  const [snapshot, setSnapshot] = useState<NativeSnapshot | null>(() => nativeBridge.latest());
  useEffect(() => { const unsubscribe = nativeBridge.subscribe(setSnapshot); if (nativeBridge.available) nativeBridge.request<NativeSnapshot>("state.get").then(setSnapshot).catch(() => {}); return unsubscribe; }, []);
  return snapshot;
}

function stableNumericId(value: string) { let hash = 0; for (let index = 0; index < value.length; index++) hash = ((hash << 5) - hash + value.charCodeAt(index)) | 0; return Math.abs(hash || 1); }
function countryFlag(code?: string) { if (!code || code.length !== 2) return "🌐"; return [...code.toUpperCase()].map(char => String.fromCodePoint(127397 + char.charCodeAt(0))).join(""); }

const initialServers: Server[] = [
  { id: 1, flag: "🇳🇱", name: "Amsterdam 01", place: "Нидерланды", protocol: "VLESS · Reality", ping: 34, load: 28, subscription: "Nova Plus", usedGb: 18.4, totalGb: 100, expires: "12 дней", kind: "subscription" },
  { id: 2, flag: "🇫🇮", name: "Helsinki 02", place: "Финляндия", protocol: "Hysteria2", ping: 41, load: 36, subscription: "Nova Plus", usedGb: 18.4, totalGb: 100, expires: "12 дней", kind: "subscription" },
  { id: 3, flag: "🇩🇪", name: "Frankfurt 03", place: "Германия", protocol: "VLESS · TLS", ping: 48, load: 52, subscription: "Nova Plus", usedGb: 18.4, totalGb: 100, expires: "12 дней", kind: "subscription" },
  { id: 4, flag: "🇸🇪", name: "Stockholm 01", place: "Швеция", protocol: "WireGuard", ping: 55, load: 43, subscription: "Nova Plus", usedGb: 18.4, totalGb: 100, expires: "12 дней", kind: "subscription" },
  { id: 5, flag: "🇫🇷", name: "Paris Private", place: "Личный конфиг", protocol: "Shadowsocks", ping: 62, load: 64, subscription: "Без подписки", usedGb: 0, totalGb: 0, expires: "Без лимита", kind: "config" },
  { id: 6, flag: "🇺🇸", name: "New York Work", place: "Личный конфиг", protocol: "Trojan · TLS", ping: 104, load: 47, subscription: "Без подписки", usedGb: 0, totalGb: 0, expires: "Без лимита", kind: "config" }
];

const services = [
  { id: "youtube", name: "YouTube", url: "https://www.youtube.com", mark: "▶", color: "#ff334e" },
  { id: "discord", name: "Discord", url: "https://discord.com", mark: "D", color: "#7189ff" },
  { id: "telegram", name: "Telegram", url: "https://telegram.org", mark: "➤", color: "#35aee2" },
  { id: "steam", name: "Steam", url: "https://store.steampowered.com", mark: "S", color: "#6da8d5" },
  { id: "spotify", name: "Spotify", url: "https://open.spotify.com", mark: "♪", color: "#52d273" },
  { id: "chatgpt", name: "ChatGPT", url: "https://chatgpt.com", mark: "✦", color: "#a7b0aa" }
];

const themes = [
  { id: "absolute-black", name: "Абсолютный чёрный", mode: "dark", bg: "#000000", panel: "#090a0d", panel2: "#0e1014", text: "#ffffff", muted: "#888f9a", accent: "#b8f356", accent2: "#5ae4be", glow: "rgba(184,243,86,.08)" },
  { id: "graphite", name: "Графит", mode: "dark", bg: "#0a0b0e", panel: "#121419", panel2: "#181b22", text: "#f5f6f8", muted: "#858b98", accent: "#a8d96d", accent2: "#5ae4be", glow: "rgba(113,148,255,.10)" },
  { id: "midnight", name: "Полночь", mode: "dark", bg: "#060913", panel: "#0d1322", panel2: "#111a2e", text: "#f4f7ff", muted: "#8592ad", accent: "#7da2ff", accent2: "#61e6d2", glow: "rgba(63,101,214,.18)" },
  { id: "crimson", name: "Красный неон", mode: "dark", bg: "#0d0709", panel: "#180d11", panel2: "#211116", text: "#fff5f6", muted: "#a4888d", accent: "#ff667d", accent2: "#ff9b78", glow: "rgba(255,54,91,.14)" },
  { id: "emerald", name: "Тёмный изумруд", mode: "dark", bg: "#050c0a", panel: "#0b1714", panel2: "#10211d", text: "#f0fff9", muted: "#7f9c92", accent: "#5ee5a2", accent2: "#a9ef72", glow: "rgba(45,190,130,.14)" },
  { id: "violet", name: "Фиолетовый карбон", mode: "dark", bg: "#0a0710", panel: "#151020", panel2: "#1d152b", text: "#fbf7ff", muted: "#9687a7", accent: "#b68cff", accent2: "#ef79c8", glow: "rgba(155,91,255,.16)" },
  { id: "amber", name: "Янтарный дым", mode: "dark", bg: "#0d0a06", panel: "#18130c", panel2: "#211a10", text: "#fffaf0", muted: "#a2957d", accent: "#ffc260", accent2: "#f08d58", glow: "rgba(255,170,58,.13)" },
  { id: "pure-light", name: "Чистый свет", mode: "light", bg: "#ffffff", panel: "#ffffff", panel2: "#f5f6f8", text: "#111318", muted: "#68707d", accent: "#6f9e25", accent2: "#159b78", glow: "rgba(0,0,0,.035)" },
  { id: "ice", name: "Ледяной голубой", mode: "light", bg: "#f4f8ff", panel: "#ffffff", panel2: "#eaf1ff", text: "#142038", muted: "#65728b", accent: "#4678e8", accent2: "#2cb6b2", glow: "rgba(69,118,231,.13)" },
  { id: "rose", name: "Белый и красный", mode: "light", bg: "#fff7f8", panel: "#ffffff", panel2: "#ffecef", text: "#2b1418", muted: "#84666c", accent: "#e8415b", accent2: "#f18472", glow: "rgba(231,65,91,.13)" },
  { id: "mint", name: "Мятное стекло", mode: "light", bg: "#f2fff9", panel: "#ffffff", panel2: "#e4f8ef", text: "#12271f", muted: "#628074", accent: "#229f70", accent2: "#74bf43", glow: "rgba(34,159,112,.12)" },
  { id: "lavender", name: "Светлая лаванда", mode: "light", bg: "#fbf7ff", panel: "#ffffff", panel2: "#f0e8fb", text: "#281b37", muted: "#786989", accent: "#8558c9", accent2: "#d45baf", glow: "rgba(133,88,201,.13)" },
  { id: "peach", name: "Персиковый крем", mode: "light", bg: "#fff9f3", panel: "#ffffff", panel2: "#ffecdc", text: "#302016", muted: "#887263", accent: "#db713e", accent2: "#d99c33", glow: "rgba(219,113,62,.13)" },
  { id: "sky", name: "Небесный градиент", mode: "light", bg: "#f4fbff", panel: "#ffffff", panel2: "#e5f5ff", text: "#122633", muted: "#627b89", accent: "#258ec1", accent2: "#557be2", glow: "rgba(37,142,193,.13)" }
] as const;

function applyTheme(theme: (typeof themes)[number]) {
  const root = document.documentElement;
  root.dataset.colorMode = theme.mode;
  root.style.setProperty("color-scheme", theme.mode);
  root.style.setProperty("--bg", theme.bg); root.style.setProperty("--panel", theme.panel); root.style.setProperty("--panel2", theme.panel2);
  root.style.setProperty("--text", theme.text); root.style.setProperty("--muted", theme.muted);
  root.style.setProperty("--subtle", theme.mode === "dark" ? "#606672" : "#7a8390");
  root.style.setProperty("--line", theme.mode === "dark" ? "rgba(255,255,255,.085)" : "rgba(20,30,45,.13)");
  root.style.setProperty("--soft", theme.mode === "dark" ? "rgba(255,255,255,.055)" : "rgba(20,30,45,.055)");
  root.style.setProperty("--hover", theme.mode === "dark" ? "rgba(255,255,255,.085)" : "rgba(20,30,45,.09)");
  root.style.setProperty("--lime", theme.accent); root.style.setProperty("--mint", theme.accent2); root.style.setProperty("--blue", theme.accent); root.style.setProperty("--violet", theme.accent2);
  root.style.setProperty("--theme-glow", theme.glow);
}

const nav: { id: Page; label: string; icon: ReactNode }[] = [
  { id: "dashboard", label: "Главная", icon: <><path d="M4 4h6v7H4zM14 4h6v4h-6zM4 15h6v5H4zM14 12h6v8h-6z"/></> },
  { id: "profiles", label: "Профили", icon: <><path d="M7 3h7l4 4v14H7zM14 3v5h5"/><path d="M10 13h5M10 17h5"/></> },
  { id: "routing", label: "Маршруты", icon: <><circle cx="6" cy="6" r="2"/><circle cx="18" cy="6" r="2"/><circle cx="12" cy="18" r="2"/><path d="M8 6h8M7 8l4 8m6-8-4 8"/></> },
  { id: "logs", label: "Журнал", icon: <path d="M5 4h14v16H5zM8 8h8M8 12h8M8 16h5"/> },
  { id: "settings", label: "Настройки", icon: <><path d="M4 7h10M18 7h2M4 17h2M10 17h10"/><circle cx="16" cy="7" r="2"/><circle cx="8" cy="17" r="2"/></> }
];

function Icon({ children, size = 20 }: { children: ReactNode; size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{children}</svg>;
}

function FlagIcon({ code, fallback }: { code?: string; fallback: string }) {
  const image = flagImage(code);
  return image
    ? <img className="country-flag-image" src={image} alt={code?.toUpperCase() || ""}/>
    : <span className="country-flag-fallback" title={fallback}><Icon size={17}><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/></Icon></span>;
}

function ServiceIcon({ service }: { service: { name: string; mark: string; color: string; presetId?: string; id?: string; iconData?: string; url?: string } }) {
  const [failed, setFailed] = useState(false);
  const brand = brandIcon(service.presetId || service.id);
  const iconUrl = brand ? "" : service.iconData || "";
  useEffect(() => setFailed(false), [iconUrl]);
  return <i className={`service-icon ${brand ? "brand" : "custom"}`} style={{ background: brand?.color || service.color }}>
    {brand ? <svg viewBox="0 0 24 24" aria-hidden="true"><path d={brand.path}/></svg>
      : iconUrl && !failed ? <img src={iconUrl} alt="" onError={() => setFailed(true)}/>
      : <Icon size={16}><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/></Icon>}
  </i>;
}

const silentNotice = (_message: string) => {};

function App() {
  const nativeState = useNativeSnapshot();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [navigationPosition, setNavigationPosition] = useState<"left" | "top">(() => window.localStorage.getItem("ior-navigation-position") === "top" ? "top" : "left");
  const [page, setPage] = useState<Page>("dashboard");
  const [selectedId, setSelectedId] = useState(1);
  const [connected, setConnected] = useState(false);
  const [connecting, setConnecting] = useState(false);
  const [connectionError, setConnectionError] = useState("");
  const completedOperationRef = useRef<string | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const [mode, setMode] = useState<"TUN" | "Proxy">("TUN");
  const [modePending, setModePending] = useState(false);
  const modePendingRef = useRef(false);
  const [importOpen, setImportOpen] = useState(false);
  const [serverItems, setServerItems] = useState<Server[]>(initialServers);
  const selected = serverItems.find(server => server.id === selectedId) ?? serverItems[0] ?? initialServers[0];

  useEffect(() => {
    if (!nativeState) return;
    const subscriptions = new Map(nativeState.subscriptions.map(item => [String(item.id), item]));
    const mapped = nativeState.servers.filter(server => !nativeState.availableServerIds || nativeState.availableServerIds.includes(server.id)).map(server => {
      const subscription = server.subscriptionId ? subscriptions.get(server.subscriptionId) : undefined;
      const totalGb = Number(server.totalBytes || subscription?.totalBytes || 0) / 1073741824;
      const usedGb = (Number(server.uploadBytes || 0) + Number(server.downloadBytes || 0)) / 1073741824;
      const expiry = server.expireUtc ? new Date(server.expireUtc) : null;
      const days = expiry ? Math.max(0, Math.ceil((expiry.getTime() - Date.now()) / 86400000)) : 0;
      return { id: stableNumericId(server.id), nativeId: server.id, subscriptionId: server.subscriptionId, countryCode: server.countryCode, flag: countryFlag(server.countryCode), name: server.name, place: server.countryName || server.server || "Сервер", protocol: server.protocol, ping: Number(server.ping || 0), load: 0, subscription: String(subscription?.name || "Без подписки"), usedGb, totalGb, expires: expiry ? `${days} дн.` : "Без лимита", kind: server.subscriptionId ? "subscription" as const : "config" as const };
    });
    setServerItems(mapped);
    if (nativeState.selectedServerId) setSelectedId(stableNumericId(nativeState.selectedServerId));
    setConnected(nativeState.connected); setConnecting(nativeState.connecting); setElapsed(nativeState.connectedSeconds);
    if (nativeState.connected || nativeState.connecting) setConnectionError("");
    if (nativeState.operation?.status === "succeeded" && completedOperationRef.current !== nativeState.operation.id) {
      completedOperationRef.current = nativeState.operation.id;
      setConnectionError("");
    }
    if (!modePendingRef.current) setMode(nativeState.settings.tunEnabled === false ? "Proxy" : "TUN");
    setNavigationPosition(String(nativeState.settings.navPanelPosition || "left").toLowerCase() === "top" ? "top" : "left");
    document.documentElement.dataset.glass = String(Boolean(nativeState.settings.usePanelGlass));
    document.documentElement.dataset.dividers = String(Boolean(nativeState.settings.showNavSeparators));
    document.documentElement.dataset.tooltips = String(Boolean(nativeState.settings.showNavTooltips));
    const nativeTheme = String(nativeState.settings.theme || "graphite").replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase();
    const aliases: Record<string, string> = { dark: "graphite", darker: "absolute-black", "red-neon": "crimson", "ice-blue": "ice", "white-red": "rose", "mint-glass": "mint", "light-lavender": "lavender", "peach-cream": "peach", "sky-gradient": "sky" };
    const theme = themes.find(item => item.id === (aliases[nativeTheme] || nativeTheme));
    if (theme) applyTheme(theme);
  }, [nativeState]);

  useEffect(() => { setConnectionError(""); }, [nativeState?.selectedServerId, nativeState?.core?.selected]);

  useEffect(() => { if (!nativeBridge.available) applyTheme(themes.find(theme => theme.id === window.localStorage.getItem("ior-theme")) ?? themes[1]); }, []);

  useEffect(() => {
    if (!connected) return;
    const timer = window.setInterval(() => setElapsed(value => value + 1), 1000);
    return () => window.clearInterval(timer);
  }, [connected]);

  const connect = async () => {
    if (connecting || modePendingRef.current) return;
    setConnectionError("");
    if (nativeBridge.available) { await nativeBridge.request("connection.toggle").catch(error => { if (!nativeBridge.latest()?.connected) setConnectionError(error instanceof Error ? error.message : String(error)); }); return; }
    if (connected) {
      setConnected(false); setElapsed(0);
      return;
    }
    if ((window.localStorage.getItem("ior-demo-core") || "singbox") !== "singbox") { setConnectionError("Это экспериментальное ядро пока не умеет подключаться. Выберите sing-box."); return; }
    setConnecting(true);
    window.setTimeout(() => {
      setConnecting(false); setConnected(true);
    }, 950);
  };

  const changeMode = async (value: "TUN" | "Proxy") => {
    if (modePendingRef.current || value === mode) return;
    setConnectionError("");
    setMode(value);
    if (nativeBridge.available) {
      modePendingRef.current = true;
      setModePending(true);
      try { await nativeBridge.request("connection.mode", { mode: value.toLowerCase() }); }
      catch (error) {
        setConnectionError(error instanceof Error ? error.message : String(error));
        setMode(nativeBridge.latest()?.settings.tunEnabled === false ? "Proxy" : "TUN");
      }
      finally { modePendingRef.current = false; setModePending(false); }
      return;
    }
  };

  const chooseServer = async (server: Server) => {
    if (nativeBridge.available && server.nativeId) {
      const runningName = nativeState?.runningServerName || "текущий сервер";
      const needsReconnect = Boolean(nativeState?.connected && nativeState.runningServerId !== server.nativeId);
      if (needsReconnect && !window.confirm(`Сейчас подключено к «${runningName}». Переподключиться к «${server.name}»?`)) return;
      try { await nativeBridge.request("server.select", { id: server.nativeId, confirmReconnect: needsReconnect }); }
      catch (error) { setConnectionError(error instanceof Error ? error.message : String(error)); }
      return;
    }
    setSelectedId(server.id);
  };

  return <div className="app">
    <Sidebar open={sidebarOpen} position={navigationPosition} page={page} onOpen={setSidebarOpen} onNavigate={setPage}/>
    <motion.main animate={{ marginLeft: navigationPosition === "top" ? 0 : sidebarOpen ? 250 : 86, paddingTop: navigationPosition === "top" ? 99 : 25 }} transition={{ type: "spring", duration: .3, bounce: .04 }}>
      <Topbar page={page} onImport={() => setImportOpen(true)}/>
      <AnimatePresence mode="wait">
        <motion.div key={page} className="page" initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -6 }} transition={{ duration: .22 }}>
          {page === "dashboard" && <Dashboard servers={serverItems} selected={selected} selectedId={selectedId} connected={connected} connecting={connecting} connectionError={connectionError} elapsed={elapsed} mode={mode} modePending={modePending} onMode={changeMode} onConnect={connect} onServer={chooseServer} onRoutingPage={() => setPage("routing")}/>} 
          {page === "profiles" && <ProfilesPage onToast={silentNotice}/>} 
          {page === "routing" && <Routing/>}
          {page === "logs" && <Logs onToast={silentNotice}/>} 
          {page === "settings" && <Settings navigationPosition={navigationPosition} onNavigationPosition={position => { setNavigationPosition(position); setSidebarOpen(false); if (!nativeBridge.available) window.localStorage.setItem("ior-navigation-position", position); }}/>} 
        </motion.div>
      </AnimatePresence>
    </motion.main>
    <AnimatePresence>{nativeState?.update && ["available", "downloading", "ready", "failed"].includes(nativeState.update.status) && <UpdateBadge update={nativeState.update} language={String(nativeState.settings.language || "russian")} />}</AnimatePresence>
<AnimatePresence>{importOpen && <ImportModal onClose={() => setImportOpen(false)} onDone={async profile => { if (nativeBridge.available) { await nativeBridge.request("server.import", { text: profile.text, name: profile.name, kind: profile.kind }); setImportOpen(false); return; } const id = Date.now(); const created: Server = { id, flag: "🌐", name: profile.name, place: profile.kind === "subscription" ? "Новая подписка" : "Личный конфиг", protocol: profile.protocol, ping: 0, load: 0, subscription: profile.kind === "subscription" ? profile.name : "Без подписки", usedGb: 0, totalGb: profile.kind === "subscription" ? 100 : 0, expires: profile.kind === "subscription" ? "30 дней" : "Без лимита", kind: profile.kind }; setServerItems(items => [...items, created]); setSelectedId(id); setImportOpen(false); }}/>}</AnimatePresence>
  </div>;
}

function UpdateBadge({ update, language }: { update: NonNullable<NativeSnapshot["update"]>; language: string }) {
  if (!nativeBridge.available) return null;
  const english = language.toLowerCase().startsWith("en");
  const downloading = update.status === "downloading";
  const ready = update.status === "ready";
  const failed = update.status === "failed";
  const title = downloading
    ? update.percent == null ? (english ? "Downloading update…" : "Загрузка обновления…") : `${english ? "Downloading update" : "Загрузка обновления"}: ${update.percent}%`
    : ready ? (english ? "Restart the app to apply the update" : "Перезапустите приложение, чтобы применить обновление")
      : failed ? `${english ? "Download failed. Click to retry" : "Не удалось скачать. Нажмите, чтобы повторить"}: ${update.error || ""}`
        : update.hasAsset ? `${english ? "Download update" : "Скачать обновление"} ${update.tag || ""}` : `${english ? "Open release page" : "Открыть страницу релиза"} ${update.tag || ""}`;
  const activate = () => {
    if (downloading) return;
    void nativeBridge.request(ready ? "app.update.apply" : "app.update.download").catch(() => undefined);
  };
  return <motion.button className={`update-badge ${downloading ? "downloading" : ""} ${ready ? "ready" : ""} ${failed ? "failed" : ""}`} type="button" onClick={activate} disabled={downloading} title={title} aria-label={title} initial={{ opacity: 0, y: 14, scale: .85 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, y: 10, scale: .9 }} whileHover={downloading ? undefined : { scale: 1.06 }} whileTap={downloading ? undefined : { scale: .94 }}>
    {downloading ? <><motion.i animate={{ rotate: 360 }} transition={{ repeat: Infinity, duration: 1.1, ease: "linear" }}/>{update.percent == null ? <Icon size={18}><path d="M12 3v12m0 0 4-4m-4 4-4-4M5 20h14"/></Icon> : <b>{update.percent}%</b>}</> : ready ? <Icon size={19}><path d="M5 12h14m-6-6 6 6-6 6"/></Icon> : failed ? <Icon size={19}><path d="M12 8v5m0 4h.01M10.3 3.9 2.7 17a2 2 0 0 0 1.7 3h15.2a2 2 0 0 0 1.7-3l-7.6-13.1a2 2 0 0 0-3.4 0Z"/></Icon> : <Icon size={19}><path d="M12 3v12m0 0 4-4m-4 4-4-4M5 20h14"/></Icon>}
  </motion.button>;
}

function Sidebar({ open, position, page, onOpen, onNavigate }: { open: boolean; position: "left" | "top"; page: Page; onOpen: (open: boolean) => void; onNavigate: (page: Page) => void }) {
  const reduceMotion = useReducedMotion();
  const labelsVisible = position === "top" || open;
  return <motion.aside className={`sidebar ${position === "top" ? "top" : ""} ${open && position === "left" ? "expanded" : ""}`} animate={{ width: position === "top" ? "100%" : open ? 250 : 86, height: position === "top" ? 74 : "100%" }} transition={reduceMotion ? { duration: 0 } : { type: "spring", duration: .3, bounce: .05 }} onMouseEnter={() => position === "left" && onOpen(true)} onMouseLeave={() => position === "left" && onOpen(false)}>
    <div className="side-head"><div className="logo">i</div><AnimatePresence>{labelsVisible && <motion.strong initial={{ opacity: 0, x: -8 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -8 }}>ior</motion.strong>}</AnimatePresence></div>
    <nav>{nav.map(item => <motion.button key={item.id} whileHover={position === "top" ? { y: -1 } : { x: 2 }} whileTap={{ scale: .97 }} className={page === item.id ? "active" : ""} onClick={() => onNavigate(item.id)} title={item.label}><span className="nav-icon"><Icon>{item.icon}</Icon></span><AnimatePresence>{labelsVisible && <motion.span className="nav-label" initial={{ opacity: 0, x: -7 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0 }}>{item.label}</motion.span>}</AnimatePresence>{page === item.id && <motion.i layoutId="nav-active"/>}</motion.button>)}</nav>
  </motion.aside>;
}

function Topbar({ page, onImport }: { page: Page; onImport: () => void }) {
  const title = { dashboard: "Главная", profiles: "Профили", routing: "Маршрутизация", logs: "Журнал", settings: "Настройки" }[page];
  return <header className="topbar"><div><span className="breadcrumb">ior / {title.toLowerCase()}</span><h1>{title}</h1></div>{page === "dashboard" && <div className="top-actions"><button className="import-button" onClick={onImport}><Icon size={17}><path d="M12 4v12m0 0 4-4m-4 4-4-4M5 20h14"/></Icon><span>Добавить профиль</span></button></div>}</header>;
}

type DashboardProps = { servers: Server[]; selected: Server; selectedId: number; connected: boolean; connecting: boolean; connectionError: string; elapsed: number; mode: "TUN" | "Proxy"; modePending: boolean; onMode: (mode: "TUN" | "Proxy") => void; onConnect: () => void; onServer: (server: Server) => void; onRoutingPage: () => void };

const coreChoices = [
  { id: "singbox", name: "sing-box", selectable: true, available: true, detail: "Рабочий сетевой движок" },
  { id: "iorcore", name: "ior-core", selectable: true, available: false, detail: "Экспериментально · соединения пока не реализованы" },
  { id: "xraycore", name: "xray-core", selectable: true, available: false, detail: "Экспериментально · адаптер пока не подключён" }
];

function CorePicker({ nativeState, connected, connecting, onSelected }: { nativeState: NativeSnapshot | null; connected: boolean; connecting: boolean; onSelected?: (id: string) => void }) {
  const reducedMotion = useReducedMotion();
  const [demoCore, setDemoCore] = useState(() => window.localStorage.getItem("ior-demo-core") || "singbox");
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(0);
  const [error, setError] = useState("");
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const buttons = useRef<Array<HTMLButtonElement | null>>([]);
  const selected = String(nativeState?.core?.selected || demoCore).toLowerCase();
  const options = nativeState?.core?.options.map(option => ({ id: option.id.toLowerCase(), name: option.name, selectable: option.selectable, available: option.available, detail: option.detail })) || coreChoices;
  const selectedOption = options.find(option => option.id === selected) || options[0];
  const activeBackend = nativeBridge.available ? nativeState?.core?.activeBackend : connected ? "sing-box" : null;
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);
  useEffect(() => { if (open) buttons.current[focused]?.focus(); }, [open, focused]);
  const choose = async (id: string) => {
    const option = options.find(item => item.id === id);
    if (!option?.selectable || connecting) return;
    setOpen(false); trigger.current?.focus(); setError("");
    if (nativeBridge.available) {
      try { await nativeBridge.request("settings.update", { selectedCore: id }); }
      catch (reason) { setError(reason instanceof Error ? reason.message : "Не удалось выбрать ядро"); }
    } else { setDemoCore(id); window.localStorage.setItem("ior-demo-core", id); onSelected?.(id); }
  };
  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === "Escape") { setOpen(false); trigger.current?.focus(); event.stopPropagation(); return; }
    if (event.key === "Tab") { setOpen(false); return; }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (!open) { setOpen(true); setFocused(Math.max(0, options.findIndex(option => option.id === selected))); }
      else setFocused(index => (index + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length);
    }
    if (event.key === "Enter" && open) { event.preventDefault(); void choose(options[focused].id); }
  };
  return <div className="core-picker" ref={root} onKeyDown={onKeyDown}>
    <button ref={trigger} type="button" className="core-picker-trigger" aria-label={`Ядро: ${selectedOption.name}${selectedOption.available ? "" : ", в разработке"}`} aria-haspopup="listbox" aria-expanded={open} disabled={connecting} onClick={() => { setOpen(value => !value); setFocused(Math.max(0, options.findIndex(option => option.id === selected))); }}><span className={`core-picker-dot ${selectedOption.available ? "" : "experimental"}`}/><span>{selectedOption.name}</span><Icon size={13}><path d="m7 9 5 5 5-5"/></Icon></button>
    <AnimatePresence>{open && <motion.div className="core-picker-menu" role="listbox" aria-label="Ядра подключения" initial={reducedMotion ? false : { opacity: 0, y: -8, scale: .96 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={reducedMotion ? undefined : { opacity: 0, y: -5, scale: .98 }} transition={{ duration: reducedMotion ? 0 : .18 }}>
      <div className="core-picker-heading">Выбор ядра</div>{options.map((option, index) => <button key={option.id} ref={element => { buttons.current[index] = element; }} type="button" role="option" aria-selected={option.id === selected} aria-disabled={!option.selectable} className={option.id === selected ? "selected" : ""} onMouseEnter={() => setFocused(index)} onClick={() => void choose(option.id)}><span><strong>{option.name}</strong><small>{option.detail}</small></span>{option.id === selected && <span className="core-picker-check">✓</span>}</button>)}
      <div className="core-picker-foot">{activeBackend ? `Запущено: ${activeBackend} · ${nativeState?.core?.protocol || ""} · ${nativeState?.core?.mode || ""}${nativeState?.core?.buildId ? ` · сборка ${nativeState.core.buildId}` : ""}` : "Сейчас ядро не запущено"}</div>
    </motion.div>}</AnimatePresence>
    {error && <span className="core-picker-error" role="alert">{error}</span>}
  </div>;
}

function Dashboard(props: DashboardProps) {
  const nativeState = useNativeSnapshot();
  const [demoSelectedCore, setDemoSelectedCore] = useState(() => window.localStorage.getItem("ior-demo-core") || "singbox");
  const { servers, selected, selectedId, connected, connecting, connectionError, elapsed, mode, modePending, onMode, onConnect, onServer, onRoutingPage } = props;
  const [activeServices, setActiveServices] = useState(["youtube", "discord", "telegram"]);
  const [servicePicker, setServicePicker] = useState(false);
  const [customServices, setCustomServices] = useState<{ id: string; name: string; mark: string; color: string }[]>([]);
  const [customSite, setCustomSite] = useState("");
  const nativeServices = nativeState?.services.map(item => { const preset = services.find(service => service.id === item.presetId); return { id: String(item.id), presetId: item.presetId ? String(item.presetId) : undefined, name: String(item.name), url: String(item.url), status: String(item.status || "unknown"), visible: item.visible !== false, iconData: item.iconData ? String(item.iconData) : undefined, mark: preset?.mark || String(item.name).slice(0, 1).toUpperCase(), color: preset?.color || "#6d7b91" }; });
  const allServices = nativeServices || [...services, ...customServices];
  useEffect(() => { if (nativeServices) setActiveServices(nativeServices.filter(item => item.visible).map(item => item.id)); }, [nativeState]);
  const toggleService = (id: string) => {
    if (!nativeBridge.available) { setActiveServices(items => items.includes(id) ? items.filter(item => item !== id) : [...items, id]); return; }
    const existing = nativeServices?.find(item => item.id === id || item.presetId === id);
    if (existing) void nativeBridge.request("service.visibility", { id: existing.id, visible: !activeServices.includes(existing.id) });
    else { const preset = services.find(item => item.id === id); if (preset) void nativeBridge.request("service.save", { id: `service-${Date.now()}`, name: preset.name, url: preset.url, presetId: preset.id }); }
  };
  const addCustomSite = () => {
    const clean = customSite.trim().replace(/^https?:\/\//, "").replace(/\/.*$/, "");
    if (!clean) return;
    const id = `custom-${clean.toLowerCase()}`;
    if (nativeBridge.available) { void nativeBridge.request("service.save", { id: `service-${Date.now()}`, name: clean, url: `https://${clean}` }); setCustomSite(""); return; }
    if (!customServices.some(service => service.id === id)) setCustomServices(items => [...items, { id, name: clean, mark: clean[0].toUpperCase(), color: "#6d7b91" }]);
    setActiveServices(items => items.includes(id) ? items : [...items, id]);
    setCustomSite("");
  };
  const removeCustomSite = (id: string) => {
    if (nativeBridge.available) { void nativeBridge.request("service.delete", { id }); return; }
    setCustomServices(items => items.filter(service => service.id !== id));
    setActiveServices(items => items.filter(item => item !== id));
  };
  const activeRoute = nativeState?.routes.find(item => String(item.id) === nativeState.activeRoutingProfileId);
  const activeRouteRules = activeRoute ? ["proxySites", "directSites", "blockSites", "proxyIp", "directIp", "blockIp"].reduce((count, key) => count + (Array.isArray(activeRoute[key]) ? activeRoute[key].length : 0), 0) : 0;
  return <div className="dashboard dashboard-v2">
    <section className="connection-workspace">
    <section className={`connect-card ${connected ? "online" : ""}`}>
      <div className="card-glow"/>
      <div className="connect-head"><span>Быстрое подключение</span><div className="connect-controls"><CorePicker nativeState={nativeState} connected={connected} connecting={connecting || modePending} onSelected={setDemoSelectedCore}/><div className={`mode-tabs ${modePending ? "applying" : ""}`} aria-busy={modePending}><button disabled={modePending} className={mode === "TUN" ? "active" : ""} onClick={() => onMode("TUN")}>TUN</button><button disabled={modePending} className={mode === "Proxy" ? "active" : ""} onClick={() => onMode("Proxy")}>Proxy</button><motion.i layoutId="mode-pill" animate={{ left: mode === "TUN" ? 4 : "50%" }} transition={{ duration: .12 }}/></div></div></div>
      <div className="connect-content"><motion.button className="power" onClick={onConnect} disabled={modePending || connecting} whileHover={{ scale: 1.035 }} whileTap={{ scale: .95 }} aria-label={connected ? "Отключиться" : "Подключиться"}><motion.span className="power-ring" animate={connecting ? { rotate: 360 } : { rotate: 0 }} transition={connecting ? { repeat: Infinity, duration: .8, ease: "linear" } : { duration: .4 }}/><Icon size={43}><path d="M12 2v10M6.3 5.6a8 8 0 1 0 11.4 0"/></Icon></motion.button><div className="connect-copy"><AnimatePresence mode="wait"><motion.h2 key={connecting ? "loading" : connected ? "on" : "off"} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -5 }}>{connecting ? "Подключаем…" : nativeState?.runtimeConnectionWarning ? "Ошибка соединения" : connected ? "Соединение защищено" : "Не подключено"}</motion.h2></AnimatePresence><p>{modePending ? `Применяем режим ${mode}…` : connected ? `Активно ${time(elapsed)}` : "Нажмите, чтобы включить защиту"}</p>{connected && nativeState?.runningServerId && nativeState.runningServerId !== nativeState.selectedServerId && <p className="running-server-note">Подключено к: {nativeState.runningServerName || "предыдущий сервер"}</p>}{(nativeState?.runtimeConnectionWarning || connectionError) && <p className="connection-error" role="alert">{nativeState?.runtimeConnectionWarning || connectionError}</p>}</div></div>
      {nativeBridge.available ? <ProfileLibraryNative servers={servers} selectedId={selectedId} onServer={onServer}/> : <ProfileLibrary servers={servers} selectedId={selectedId} onServer={onServer}/>} 
    </section>
    </section>
    <section className="network-dock"><div className="network-title"><span>Сеть</span><strong>{connected ? String(nativeState?.traffic.quality || "Трафик активен") : "Ожидание подключения"}</strong><small>{mode} · DNS {String(nativeState?.settings.dnsServer || "1.1.1.1")}</small></div><div className="network-metrics"><CompactStat label="Входящая" value={String(nativeState?.traffic.download || "0 KB/s")} unit="" accent="blue"/><CompactStat label="Исходящая" value={String(nativeState?.traffic.upload || "0 KB/s")} unit="" accent="violet"/><CompactStat label="Пинг" value={String(nativeState?.traffic.ping ?? selected.ping ?? "-")} unit="ms" accent="mint"/><CompactStat label="Потери" value="0.0" unit="%" accent="orange"/></div><div className="dock-services"><div className="dock-service-list">{allServices.filter(service => activeServices.includes(service.id)).map(service => <motion.div layout key={service.id} className="dock-service" data-status={"status" in service ? String(service.status) : "unknown"} title={`${service.name}: ${"status" in service && service.status === "available" ? "доступен" : "status" in service && service.status === "failed" ? "ошибка проверки" : "status" in service && service.status === "warning" ? "проверка не прошла" : "проверяется"}`}><ServiceIcon service={service}/><b/></motion.div>)}</div><button onClick={() => setServicePicker(value => !value)} aria-expanded={servicePicker}><Icon size={16}><path d="M4 7h10M18 7h2M4 17h2M10 17h10"/><circle cx="16" cy="7" r="2"/><circle cx="8" cy="17" r="2"/></Icon></button><AnimatePresence>{servicePicker && <motion.div className="service-picker dock-picker" initial={{ opacity: 0, y: -7, scale: .97 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, y: -5, scale: .98 }} transition={{ type: "spring", stiffness: 390, damping: 31 }}><div className="custom-site-copy"><strong>Отслеживаемые сервисы</strong><small>Введите домен нужного сайта</small></div><form className="custom-site-form" onSubmit={event => { event.preventDefault(); addCustomSite(); }}><input value={customSite} onChange={event => setCustomSite(event.target.value)} placeholder="Например, example.com" aria-label="Сайт для отслеживания"/><button type="submit">Добавить</button></form><div className="service-options">{allServices.map(service => <motion.div layout="position" key={service.id} className={`service-option ${activeServices.includes(service.id) ? "selected" : ""}`}><button className="service-toggle" onClick={() => toggleService(service.id)}><ServiceIcon service={service}/><span>{service.name}</span><b><Icon size={12}><path d="m5 12 4 4 10-10"/></Icon></b></button><motion.button className="remove-service" onClick={() => removeCustomSite(service.id)} initial={{ opacity: 0, scale: .7 }} animate={{ opacity: 1, scale: 1 }} exit={{ opacity: 0, scale: .7 }} aria-label={`Удалить ${service.name}`}><Icon size={13}><path d="M4 7h16M9 7V4h6v3M7 7l1 14h8l1-14M10 11v6M14 11v6"/></Icon></motion.button></motion.div>)}</div></motion.div>}</AnimatePresence></div></section>
    <section className="route-card small-card"><div className="small-icon route-icon"><Icon><circle cx="6" cy="6" r="2"/><circle cx="18" cy="6" r="2"/><circle cx="12" cy="18" r="2"/><path d="M8 6h8M7 8l4 8m6-8-4 8"/></Icon></div><div><span>Маршрутизация</span><h3>{String(activeRoute?.name || "Без маршрутизации")}</h3><p>{activeRouteRules} правил · DNS {String(nativeState?.settings.dnsServer || "1.1.1.1")}</p></div><button onClick={onRoutingPage}>Открыть</button></section>
  </div>;
}

type ServerRowProps = { server: Server; selected: boolean; displayName: string; latency?: number | null; onSelect: () => void; onContext: (event: React.MouseEvent) => void };

function ProfileServerRow({ server, selected, displayName, latency, onSelect, onContext }: ServerRowProps) {
  return <motion.button className={`profile-server-row ${selected ? "selected" : ""}`} onClick={onSelect} onContextMenu={onContext} whileHover={{ x: 2 }} layout="position"><span className="flag"><FlagIcon code={server.countryCode} fallback={server.flag}/></span><span><strong>{displayName}</strong><small>{server.protocol}</small></span><em>{latency === null ? "…" : `${latency ?? server.ping} ms`}</em>{selected && <motion.i className="server-selected-bar" layoutId="selected-server-indicator" transition={{ type: "spring", stiffness: 520, damping: 38, mass: .72 }}/>}</motion.button>;
}

function SubscriptionMetadata({ subscription }: { subscription: Record<string, any> }) {
  const supportUrl = typeof subscription.supportUrl === "string" ? subscription.supportUrl : "";
  const profileUrl = typeof subscription.profileWebPageUrl === "string" ? subscription.profileWebPageUrl : "";
  const announcement = typeof subscription.announcement === "string" ? subscription.announcement.trim() : "";
  if (!supportUrl && !profileUrl && !announcement) return null;
  const openLink = (event: React.MouseEvent<HTMLAnchorElement>, url: string) => {
    event.stopPropagation();
    if (!nativeBridge.available) return;
    event.preventDefault();
    void nativeBridge.request("external.open", { url }).catch(error => console.error(error));
  };
  return <div className="subscription-metadata">
    {(supportUrl || profileUrl) && <div className="subscription-links">
      {supportUrl && <a href={supportUrl} target="_blank" rel="noopener noreferrer" title={supportUrl} onClick={event => openLink(event, supportUrl)}><Icon size={13}><path d="M5 11a7 7 0 0 1 14 0v5a2 2 0 0 1-2 2h-2v-6h4M5 12h4v6H7a2 2 0 0 1-2-2z"/></Icon>Поддержка</a>}
      {profileUrl && <a href={profileUrl} target="_blank" rel="noopener noreferrer" title={profileUrl} onClick={event => openLink(event, profileUrl)}><Icon size={13}><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/></Icon>Страница профиля</a>}
    </div>}
    {announcement && <div className="subscription-announcement"><Icon size={14}><path d="M4 10v4h3l9 5V5l-9 5H4zM19 9a4 4 0 0 1 0 6"/></Icon><span>{announcement}</span></div>}
  </div>;
}

function formatQuotaGb(bytes: number): string {
  const gb = Math.max(0, Number.isFinite(bytes) ? bytes : 0) / 1073741824;
  return gb >= 10 ? gb.toFixed(1) : gb >= 1 ? gb.toFixed(2) : gb.toFixed(3).replace(/0+$/, "").replace(/\.$/, ".0");
}

function SubscriptionQuota({ uploadBytes, downloadBytes, totalBytes, known }: { uploadBytes?: number; downloadBytes?: number; totalBytes?: number; known: boolean }) {
  const used = Math.max(0, Number(uploadBytes || 0)) + Math.max(0, Number(downloadBytes || 0));
  const total = Math.max(0, Number(totalBytes || 0));
  if (!known) return <span className="subscription-quota quota-unknown">Лимит не указан</span>;
  if (total === 0) return <span className="subscription-quota quota-unlimited" aria-label={`Безлимитный трафик, потрачено ${formatQuotaGb(used)} гигабайт`}><span className="quota-infinity" aria-hidden="true">∞</span><span><b>Безлимит</b><small>Потрачено {formatQuotaGb(used)} ГБ</small></span></span>;
  const percent = Math.min(100, used / total * 100);
  return <span className="subscription-quota quota-limited" aria-label={`Потрачено ${formatQuotaGb(used)} из ${formatQuotaGb(total)} гигабайт`}><b>{formatQuotaGb(used)} / {formatQuotaGb(total)} ГБ</b><i><em style={{ width: `${percent}%` }}/></i></span>;
}

function ProfileLibraryNative({ servers, selectedId, onServer }: { servers: Server[]; selectedId: number; onServer: (server: Server) => void }) {
  const state = useNativeSnapshot();
  const activeWorkspace = state?.connectionProfiles.find(profile => String(profile.id) === state.activeConnectionProfileId);
  const visibleSubscriptions = (state?.subscriptions || []).filter(subscription =>
    !activeWorkspace || String(subscription.id) === String(activeWorkspace.subscriptionId || "") ||
    servers.some(server => server.subscriptionId === String(subscription.id)));
  const [openGroups, setOpenGroups] = useState<Record<string, boolean>>({});
  const [query, setQuery] = useState("");
  const [menu, setMenu] = useState<{ kind: "server" | "subscription"; id: string; name: string; x: number; y: number } | null>(null);
  const [editingServer, setEditingServer] = useState<Record<string, any> | null>(null);
  const [previewServer, setPreviewServer] = useState<{ id: string; name: string; json: string; status: string } | null>(null);
  const [editError, setEditError] = useState("");
  useEffect(() => {
    if (!previewServer) return;
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === "Escape") setPreviewServer(null); };
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [previewServer]);
  useEffect(() => {
    if (!editingServer) return;
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === "Escape") setEditingServer(null); };
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [editingServer]);
  useEffect(() => { if (!menu) return; const close = () => setMenu(null); window.addEventListener("pointerdown", close); return () => window.removeEventListener("pointerdown", close); }, [menu]);
  const matches = (server: Server) => `${server.name} ${server.protocol} ${server.place}`.toLowerCase().includes(query.toLowerCase());
  const context = (event: React.MouseEvent, kind: "server" | "subscription", id: string, name: string) => { event.preventDefault(); event.stopPropagation(); setMenu({ kind, id, name, x: Math.max(12, Math.min(event.clientX, window.innerWidth - 230)), y: Math.max(12, Math.min(event.clientY, window.innerHeight - 220)) }); };
  const rename = async () => { if (!menu) return; const name = window.prompt("Новое название", menu.name)?.trim(); if (name) await nativeBridge.request(menu.kind === "server" ? "server.rename" : "subscription.rename", { id: menu.id, name }); setMenu(null); };
  const remove = async () => { if (!menu) return; await nativeBridge.request(menu.kind === "server" ? "server.delete" : "subscription.delete", { id: menu.id }); setMenu(null); };
  const openServerEditor = async (copy: boolean) => { if (!menu) return; try { const profile = await nativeBridge.request<Record<string, any>>(copy ? "server.copy" : "server.get", { id: menu.id }); setEditingServer(profile); setEditError(""); setMenu(null); } catch (error) { setEditError(String(error)); } };
  const saveServerEditor = async () => { if (!editingServer) return; try { await nativeBridge.request("server.save", editingServer); setEditingServer(null); setEditError(""); } catch (error) { setEditError(error instanceof Error ? error.message : String(error)); } };
  const openConfigPreview = async () => { if (!menu) return; const target = menu; setMenu(null); try { const json = await nativeBridge.request<string>("config.preview", { id: target.id }); setPreviewServer({ id: target.id, name: target.name, json, status: "Не проверено" }); } catch (error) { window.alert(error instanceof Error ? error.message : String(error)); } };
  const verifyConfigPreview = async () => { if (!previewServer) return; setPreviewServer(value => value && { ...value, status: "Проверяем…" }); try { const result = await nativeBridge.request<{ valid: boolean; output: string }>("config.validatePreview", { id: previewServer.id }); setPreviewServer(value => value && { ...value, status: result.valid ? "Конфигурация корректна" : `Ошибка: ${result.output}` }); } catch (error) { setPreviewServer(value => value && { ...value, status: error instanceof Error ? error.message : String(error) }); } };
  const standalone = servers.filter(server => !server.subscriptionId && matches(server));
  return <section className="profile-library inline-library"><div className="library-bar"><span>Серверы и профили</span><div className="library-tools"><label><Icon size={13}><circle cx="11" cy="11" r="7"/><path d="m16 16 5 5"/></Icon><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Поиск"/></label></div></div>
    {visibleSubscriptions.map(subscription => {
      const id = String(subscription.id);
      const group = servers.filter(server => server.subscriptionId === id && matches(server));
      const open = openGroups[id] !== false;
      const refresh = subscriptionStatus(subscription, state?.settings.autoRefreshSubscriptions !== false);
      const importSummary = `${subscription.lastImportedServers != null ? ` · ${subscription.lastImportedServers} серверов` : ""}${subscription.lastRejectedServers ? ` · ${subscription.lastRejectedServers} пропущено` : ""}`;
      const usageKnown = Boolean(subscription.hasUsageInfo || Number(subscription.totalBytes || 0) > 0);
      return <motion.article layout key={id} className="subscription-group" onContextMenu={event => context(event, "subscription", id, String(subscription.name))}>
        <button className="subscription-toggle" onClick={() => setOpenGroups(items => ({ ...items, [id]: !open }))}>
          <span><small>Подписка</small><strong>{String(subscription.name)}</strong><em>{group.length} серверов</em><span className="subscription-refresh-time">{refresh.last}{importSummary}<br/>{refresh.next}{refresh.error && <><br/>{refresh.error}</>}</span></span>
          <SubscriptionQuota known={usageKnown} uploadBytes={subscription.uploadBytes} downloadBytes={subscription.downloadBytes} totalBytes={subscription.totalBytes}/>
          <motion.span className="collapse-chevron" animate={{ rotate: open ? 180 : 0 }}><Icon size={16}><path d="m6 9 6 6 6-6"/></Icon></motion.span>
        </button>
        <SubscriptionMetadata subscription={subscription}/>
        <AnimatePresence initial={false}>{open && <motion.div className="profile-server-list" initial={{ height: 0, opacity: 0 }} animate={{ height: "auto", opacity: 1 }} exit={{ height: 0, opacity: 0 }}>{group.map(server => <ProfileServerRow key={server.id} server={server} selected={server.id === selectedId} displayName={server.name} onSelect={() => onServer(server)} onContext={event => context(event, "server", server.nativeId!, server.name)}/>)}</motion.div>}</AnimatePresence>
      </motion.article>;
    })}
    <div className="standalone-configs"><div className="standalone-heading"><span>Личные конфиги</span></div><div className="profile-server-list config-list">{standalone.map(server => <ProfileServerRow key={server.id} server={server} selected={server.id === selectedId} displayName={server.name} onSelect={() => onServer(server)} onContext={event => context(event, "server", server.nativeId!, server.name)}/>)}</div></div>
    <AnimatePresence>{menu && <motion.div className="profile-context-menu" style={{ left: menu.x, top: menu.y }} initial={{ opacity: 0, scale: .78, filter: "blur(8px)" }} animate={{ opacity: 1, scale: 1, filter: "blur(0)" }} exit={{ opacity: 0, scale: .9 }} onPointerDown={event => event.stopPropagation()}><div className="context-title"><small>{menu.kind === "server" ? "Конфигурация" : "Подписка"}</small><strong>{menu.name}</strong></div><button onClick={rename}>Переименовать</button>{menu.kind === "server" && <>{state?.servers.find(item => String(item.id) === menu.id)?.subscriptionId ? <button onClick={() => void openServerEditor(true)}>Создать локальную копию</button> : <button onClick={() => void openServerEditor(false)}>Изменить конфиг</button>}<button onClick={() => void openConfigPreview()}>Просмотр и проверка конфига</button></>}{menu.kind === "subscription" && <button onClick={() => nativeBridge.request("subscription.refresh", { id: menu.id }).then(() => setMenu(null))}>Обновить сейчас</button>}<i/><button className="danger" onClick={remove}>Удалить</button></motion.div>}</AnimatePresence>
    {createPortal(<AnimatePresence>{previewServer && <motion.div className="modal-layer config-preview-layer" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onMouseDown={() => setPreviewServer(null)}><motion.div className="modal config-preview-modal" role="dialog" aria-modal="true" aria-label={`Итоговый конфиг: ${previewServer.name}`} initial={{ opacity: 0, y: 12, scale: .97 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, y: 8, scale: .98 }} onMouseDown={event => event.stopPropagation()}><div className="modal-head"><div><span>Итоговый конфиг {String(state?.core?.selected || "sing-box").toLowerCase() === "xraycore" ? "Xray" : "sing-box"}</span><h2>{previewServer.name}</h2></div><button onClick={() => setPreviewServer(null)} aria-label="Закрыть">×</button></div><p className="config-preview-status" role="status">{previewServer.status}</p><pre>{previewServer.json}</pre><div className="modal-actions"><button type="button" onClick={() => void verifyConfigPreview()}>Проверить</button><button type="button" onClick={() => void navigator.clipboard?.writeText(previewServer.json)}>Копировать</button><button type="button" className="primary" onClick={() => { const url = URL.createObjectURL(new Blob([previewServer.json], { type: "application/json" })); const link = document.createElement("a"); link.href = url; link.download = "ior-config.json"; link.click(); URL.revokeObjectURL(url); }}>Сохранить JSON</button></div></motion.div></motion.div>}</AnimatePresence>, document.body)}
    {createPortal(<AnimatePresence>{editingServer && <><motion.div className="drawer-backdrop server-editor-backdrop" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={() => setEditingServer(null)}/><motion.aside className="profile-drawer server-editor-drawer" role="dialog" aria-modal="true" aria-label={`Изменить ${String(editingServer.protocol)}`} initial={{ x: "100%" }} animate={{ x: 0 }} exit={{ x: "100%" }} transition={{ type: "spring", stiffness: 360, damping: 38 }}><header><div><span>Личный конфиг</span><h2>Изменить {String(editingServer.protocol)}</h2></div><button onClick={() => setEditingServer(null)}>×</button></header><div className="drawer-scroll"><ServerFields profile={editingServer} onChange={setEditingServer}/>{editError && <p className="import-error">{editError}</p>}</div><footer><button onClick={() => setEditingServer(null)}>Отмена</button><button className="primary" onClick={() => void saveServerEditor()}>Сохранить</button></footer></motion.aside></>}</AnimatePresence>, document.body)}
  </section>;
}

function ServerFields({ profile, onChange }: { profile: Record<string, any>; onChange: (value: Record<string, any>) => void }) {
  const [revealedSecrets, setRevealedSecrets] = useState<Record<string, boolean>>({});
  const protocol = String(profile.protocol || "").toLowerCase();
  const common = [["name", "Название"], ["server", "Адрес сервера"], ["port", "Порт"]];
  const fields = protocol === "vless" ? [["userId", "UUID"], ["flow", "Flow"], ["serverName", "SNI"], ["publicKey", "Reality public key"], ["shortId", "Short ID"], ["fingerprint", "Fingerprint"], ["network", "Транспорт"], ["host", "Host"], ["path", "Путь WS/HTTP"], ["serviceName", "gRPC service"]]
    : protocol === "hysteria2" ? [["password", "Пароль"], ["serverName", "SNI"], ["obfsType", "Obfs type"], ["obfsPassword", "Obfs password"]]
    : protocol === "wireguard" ? [["privateKey", "Private key"], ["peerPublicKey", "Peer public key"], ["preSharedKey", "Pre-shared key"], ["localAddress", "Локальный адрес"], ["allowedIps", "Allowed IPs"], ["persistentKeepalive", "Keepalive"]]
    : protocol === "shadowsocks" ? [["method", "Шифр"], ["password", "Пароль"]]
    : protocol === "trojan" ? [["password", "Пароль"], ["serverName", "SNI"], ["network", "Транспорт"], ["host", "Host"], ["path", "Путь"]]
    : protocol === "socks" || protocol === "http" ? [["username", "Имя пользователя"], ["password", "Пароль"], ["serverName", "SNI"]]
    : [["userId", "UUID"], ["security", "Security"], ["alterId", "Alter ID"], ["serverName", "SNI"], ["network", "Транспорт"], ["host", "Host"], ["path", "Путь"]];
  const bools = protocol === "vless" || protocol === "vmess" ? [["tlsEnabled", "TLS"], ["realityEnabled", "Reality"], ["allowInsecure", "Не проверять сертификат"]] : protocol === "hysteria2" || protocol === "trojan" ? [["allowInsecure", "Не проверять сертификат"]] : protocol === "http" || protocol === "socks" ? [["tlsEnabled", "TLS"], ["allowInsecure", "Не проверять сертификат"]] : [];
  return <div className="server-editor-fields">{[...common, ...fields].map(([key, label]) => { const secret = ["userId", "password", "obfsPassword", "privateKey", "preSharedKey"].includes(key); return <label className="drawer-field" key={key}><span>{label}</span><span className={secret ? "secret-field" : "ordinary-field"}><input type={secret && !revealedSecrets[key] ? "password" : key === "port" || key === "alterId" || key === "persistentKeepalive" ? "number" : "text"} autoComplete={secret ? "off" : undefined} value={profile[key] ?? ""} onChange={event => onChange({ ...profile, [key]: event.target.type === "number" ? Number(event.target.value) : event.target.value })}/>{secret && <button type="button" aria-label={revealedSecrets[key] ? `Скрыть ${label}` : `Показать ${label}`} onClick={() => setRevealedSecrets(current => ({ ...current, [key]: !current[key] }))}>{revealedSecrets[key] ? "Скрыть" : "Показать"}</button>}</span></label>; })}{bools.map(([key, label]) => <label className="server-editor-check" key={key}><input type="checkbox" checked={Boolean(profile[key])} onChange={event => onChange({ ...profile, [key]: event.target.checked })}/><span>{label}</span></label>)}</div>;
}

function ProfileLibrary({ servers, selectedId, onServer }: { servers: Server[]; selectedId: number; onServer: (server: Server) => void }) {
  const subscriptionServers = servers.filter(server => server.kind === "subscription");
  const configs = servers.filter(server => server.kind === "config");
  const [subscriptionOpen, setSubscriptionOpen] = useState(true);
  const [subscriptionName, setSubscriptionName] = useState("Nova Plus");
  const [renamedServers, setRenamedServers] = useState<Record<number, string>>({});
  const [hiddenServers, setHiddenServers] = useState<number[]>([]);
  const [subscriptionVisible, setSubscriptionVisible] = useState(true);
  const [updatedNow, setUpdatedNow] = useState(false);
  const [contextMenu, setContextMenu] = useState<{ kind: "subscription" | "server"; id?: number; name: string; x: number; y: number; originX: number; originY: number } | null>(null);
  const [renameDraft, setRenameDraft] = useState("");
  const [renaming, setRenaming] = useState(false);
  const [serverQuery, setServerQuery] = useState("");
  const [latencies, setLatencies] = useState<Record<number, number | null>>({});
  const refreshHours = Number(window.localStorage.getItem("ior-refresh-nova") || 6);
  const refreshGloballyEnabled = window.localStorage.getItem("ior-refresh-global") !== "false";
  const refreshSubscriptionEnabled = window.localStorage.getItem("ior-refresh-enabled-nova") !== "false";
  const storedNextUpdate = Number(window.localStorage.getItem("ior-refresh-next-nova")) || Date.now() + refreshHours * 60 * 60 * 1000;
  const nextUpdateDate = new Date(storedNextUpdate);
  const nextUpdate = `${nextUpdateDate.toLocaleDateString("ru-RU", { day: "numeric", month: "short" })}, ${nextUpdateDate.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" })}`;
  const updateSubscriptionNow = () => { const now = Date.now(); window.localStorage.setItem("ior-refresh-last-nova", String(now)); window.localStorage.setItem("ior-refresh-next-nova", String(now + refreshHours * 60 * 60 * 1000)); setUpdatedNow(true); setContextMenu(null); };

  useEffect(() => {
    if (!contextMenu) return;
    const close = () => { setContextMenu(null); setRenaming(false); };
    window.addEventListener("pointerdown", close);
    window.addEventListener("blur", close);
    return () => { window.removeEventListener("pointerdown", close); window.removeEventListener("blur", close); };
  }, [contextMenu]);

  const openContext = (event: React.MouseEvent, target: { kind: "subscription" | "server"; id?: number; name: string }) => {
    event.preventDefault(); event.stopPropagation(); setRenaming(false); setRenameDraft(target.name);
    const x = Math.max(10, Math.min(event.clientX, window.innerWidth - 225));
    const y = Math.max(10, Math.min(event.clientY, window.innerHeight - 250));
    setContextMenu({ ...target, x, y, originX: Math.max(0, Math.min(1, (event.clientX - x) / 210)), originY: Math.max(0, Math.min(1, (event.clientY - y) / 220)) });
  };
  const saveRename = () => {
    if (!contextMenu || !renameDraft.trim()) return;
    if (contextMenu.kind === "subscription") setSubscriptionName(renameDraft.trim());
    else if (contextMenu.id) setRenamedServers(items => ({ ...items, [contextMenu.id!]: renameDraft.trim() }));
    setContextMenu(null); setRenaming(false);
  };
  const removeContextTarget = () => {
    if (!contextMenu) return;
    if (contextMenu.kind === "subscription") setSubscriptionVisible(false);
    else if (contextMenu.id) setHiddenServers(items => [...items, contextMenu.id!]);
    setContextMenu(null);
  };
  const testLatency = (id: number) => { setLatencies(items => ({ ...items, [id]: null })); setContextMenu(null); window.setTimeout(() => setLatencies(items => ({ ...items, [id]: 24 + Math.floor(Math.random() * 88) })), 700); };
  const row = (server: Server) => <ProfileServerRow key={server.id} server={server} selected={server.id === selectedId} displayName={renamedServers[server.id] || server.name} latency={Object.prototype.hasOwnProperty.call(latencies, server.id) ? latencies[server.id] : undefined} onSelect={() => onServer(server)} onContext={event => openContext(event, { kind: "server", id: server.id, name: renamedServers[server.id] || server.name })}/>;
  const matches = (server: Server) => `${renamedServers[server.id] || server.name} ${server.protocol} ${server.place}`.toLowerCase().includes(serverQuery.toLowerCase());
  return <section className="profile-library inline-library">
    <div className="library-bar"><span>Серверы и профили</span><div className="library-tools"><label><Icon size={13}><circle cx="11" cy="11" r="7"/><path d="m16 16 5 5"/></Icon><input value={serverQuery} onChange={event => setServerQuery(event.target.value)} placeholder="Поиск"/></label></div></div>
    <AnimatePresence initial={false}>{subscriptionVisible && <motion.article className="subscription-group" initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} onContextMenu={event => openContext(event, { kind: "subscription", name: subscriptionName })}>
      <button className="subscription-toggle" onClick={() => setSubscriptionOpen(value => !value)} aria-expanded={subscriptionOpen}>
        <span><small>Подписка</small><strong>{subscriptionName}</strong><em>{subscriptionServers.filter(server => !hiddenServers.includes(server.id)).length} сервера · ещё 12 дней</em><span className="subscription-refresh-time">{!refreshGloballyEnabled || !refreshSubscriptionEnabled ? "Автообновление отключено" : updatedNow ? "обновлено только что" : `обновление через ${refreshHours} ч · ${nextUpdate}`}</span></span>
        <SubscriptionQuota known uploadBytes={18.4 * 1073741824} downloadBytes={0} totalBytes={100 * 1073741824}/>
        <motion.span className="collapse-chevron" animate={{ rotate: subscriptionOpen ? 180 : 0 }}><Icon size={16}><path d="m6 9 6 6 6-6"/></Icon></motion.span>
      </button>
      <AnimatePresence initial={false}>{subscriptionOpen && <motion.div className="profile-server-list" initial={{ height: 0, opacity: 0 }} animate={{ height: "auto", opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ type: "spring", duration: .28, bounce: .03 }}>{subscriptionServers.filter(server => !hiddenServers.includes(server.id) && matches(server)).map(row)}</motion.div>}</AnimatePresence>
    </motion.article>}</AnimatePresence>
    <div className="standalone-configs"><div className="standalone-heading"><span>Личные конфиги</span></div><div className="profile-server-list config-list">{configs.filter(server => !hiddenServers.includes(server.id) && matches(server)).map(row)}</div></div>
    <AnimatePresence>{contextMenu && <motion.div className="profile-context-menu" style={{ left: contextMenu.x, top: contextMenu.y, transformOrigin: `${contextMenu.originX * 100}% ${contextMenu.originY * 100}%` }} initial={{ opacity: 0, scale: .78, y: 5, filter: "blur(9px)" }} animate={{ opacity: 1, scale: 1, y: 0, filter: "blur(0px)" }} exit={{ opacity: 0, scale: .9, y: 3, filter: "blur(5px)" }} transition={{ type: "spring", stiffness: 520, damping: 34, mass: .72 }} onPointerDown={event => event.stopPropagation()}>{renaming ? <form className="context-rename" onSubmit={event => { event.preventDefault(); saveRename(); }}><label>Новое название</label><input value={renameDraft} onChange={event => setRenameDraft(event.target.value)} autoFocus/><div><button type="button" onClick={() => setRenaming(false)}>Отмена</button><button type="submit">Сохранить</button></div></form> : <><div className="context-title"><small>{contextMenu.kind === "subscription" ? "Подписка" : "Конфигурация"}</small><strong>{contextMenu.name}</strong></div><button onClick={() => setRenaming(true)}><Icon size={15}><path d="m4 20 4-1 11-11-3-3L5 16zM14 6l3 3"/></Icon>Переименовать</button>{contextMenu.kind === "subscription" ? <button onClick={updateSubscriptionNow}><Icon size={15}><path d="M20 6v5h-5M4 18v-5h5M18 9a7 7 0 0 0-12-2M6 15a7 7 0 0 0 12 2"/></Icon>Обновить сейчас</button> : <><button onClick={() => contextMenu.id && testLatency(contextMenu.id)}><Icon size={15}><path d="M4 15a8 8 0 0 1 16 0M7 15a5 5 0 0 1 10 0M10 15a2 2 0 0 1 4 0"/></Icon>Проверить задержку</button><button onClick={() => setContextMenu(null)}><Icon size={15}><path d="M8 8h11v12H8zM5 16H4V4h11v1"/></Icon>Копировать ссылку</button></>}<i/><button className="danger" onClick={removeContextTarget}><Icon size={15}><path d="M4 7h16M9 7V4h6v3M7 7l1 14h8l1-14"/></Icon>Удалить</button></>}</motion.div>}</AnimatePresence>
  </section>;
}

function Stat({ label, value, unit, accent }: { label: string; value: string; unit: string; accent: string }) { return <div className="stat"><span><i className={accent}/>{label}</span><strong>{value}<small>{unit}</small></strong></div>; }
function CompactStat({ label, value, unit, accent }: { label: string; value: string; unit: string; accent: string }) { return <div className="compact-stat"><i className={accent}/><span><small>{label}</small><strong>{value}<em>{unit}</em></strong></span></div>; }

type SavedProfile = { id: string; name: string; subscriptionId: string; routingProfileId: string; defaultServerId?: string; configIds: string[]; color: string; iorTrafficMode?: string };

const demoSubscriptionOptions = [{ id: "nova", name: "Nova Plus" }, { id: "work", name: "Work Space" }, { id: "none", name: "Без подписки" }];
const baseConfigOptions = ["Paris Private", "New York Work", "WireGuard Home"];

function ProfilesPage({ onToast }: { onToast: (message: string) => void }) {
  const nativeState = useNativeSnapshot();
  const [profiles, setProfiles] = useState<SavedProfile[]>(() => { try { const saved = window.localStorage.getItem("ior-connection-profiles"); if (saved) { const parsed = JSON.parse(saved); if (Array.isArray(parsed)) return parsed.map((item: any, index: number) => ({ id: String(item.id || `profile-${Date.now()}-${index}`), name: String(item.name || `Профиль ${index + 1}`), subscriptionId: String(item.subscriptionId || (item.subscription ? "nova" : "none")), routingProfileId: String(item.routingProfileId || "smart"), configIds: Array.isArray(item.configIds) ? item.configIds.map(String) : [], color: String(item.color || "#b8f356") })); } } catch {} return [
    { id: "demo-1", name: "Основной", subscriptionId: "nova", routingProfileId: "smart", configIds: ["Paris Private", "New York Work"], color: "#b8f356" },
    { id: "demo-2", name: "Работа", subscriptionId: "work", routingProfileId: "work", configIds: ["New York Work"], color: "#7194ff" },
    { id: "demo-3", name: "Минимальный", subscriptionId: "none", routingProfileId: "direct", configIds: ["Paris Private"], color: "#a68cff" }
  ]; });
  const [activeId, setActiveId] = useState("demo-1");
  const [createOpen, setCreateOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState<SavedProfile | null>(null);
  const [configOptions, setConfigOptions] = useState(baseConfigOptions);
  const [newConfig, setNewConfig] = useState("");
  const [name, setName] = useState("");
  const [template, setTemplate] = useState<"full" | "clean">("full");
  useEffect(() => { if (!nativeBridge.available) window.localStorage.setItem("ior-connection-profiles", JSON.stringify(profiles)); }, [profiles]);
  useEffect(() => {
    if (!nativeState) return;
    const hex = (value: unknown) => `#${(Number(value) >>> 0).toString(16).padStart(8, "0").slice(2)}`;
    const serverNames = new Map(nativeState.servers.map(server => [server.id, server.name]));
    setProfiles(nativeState.connectionProfiles.map(item => ({ id: String(item.id), name: String(item.name), subscriptionId: String(item.subscriptionId || "none"), routingProfileId: String(item.routingProfileId || ""), defaultServerId: item.defaultServerId ? String(item.defaultServerId) : undefined, configIds: Array.isArray(item.configIds) ? item.configIds.map((id: unknown) => serverNames.get(String(id)) || String(id)) : [], color: hex(item.colorArgb), iorTrafficMode: String(item.iorTrafficMode || "standard") })));
    if (nativeState.activeConnectionProfileId) setActiveId(nativeState.activeConnectionProfileId);
    setConfigOptions(nativeState.servers.filter(item => !item.subscriptionId).map(item => item.name));
  }, [nativeState]);
  useEffect(() => { if (!editOpen) return; const close = (event: KeyboardEvent) => { if (event.key === "Escape") { setEditOpen(false); setDraft(null); } }; window.addEventListener("keydown", close); return () => window.removeEventListener("keydown", close); }, [editOpen]);
  const routingOptions = nativeState ? nativeState.routes.map(item => ({ id: String(item.id), name: String(item.name) })) : (() => { try { const saved = JSON.parse(window.localStorage.getItem("ior-routing-profiles") || "[]"); if (Array.isArray(saved) && saved.length) return saved.map((item: { id: string; name: string }) => ({ id: item.id, name: item.name })); } catch {} return [{ id: "smart", name: "Умный маршрут" }, { id: "work", name: "Корпоративный" }, { id: "direct", name: "Прямой" }]; })();
  const availableSubscriptions = nativeState ? [{ id: "none", name: "Без подписки" }, ...nativeState.subscriptions.map(item => ({ id: String(item.id), name: String(item.name) }))] : demoSubscriptionOptions;
  const subscriptionOptions = availableSubscriptions;
  const subscriptionName = (id: string) => availableSubscriptions.find(item => item.id === id)?.name || "Без подписки";
  const routeName = (id: string) => routingOptions.find((item: { id: string; name: string }) => item.id === id)?.name || "Без маршрутизации";
  const createProfile = () => {
    const title = name.trim() || `Профиль ${profiles.length + 1}`;
    const profile: SavedProfile = { id: `profile-${Date.now()}`, name: title, subscriptionId: template === "full" ? availableSubscriptions.find(item => item.id !== "none")?.id || "none" : "none", routingProfileId: template === "full" ? routingOptions[0]?.id || "" : "", configIds: template === "full" ? configOptions : [], color: template === "full" ? "#5ae4be" : "#ffb86c" };
    if (nativeBridge.available) void saveNativeProfile(profile).then(() => nativeBridge.request("connectionProfile.select", { id: profile.id })); else { setProfiles(items => [...items, profile]); setActiveId(profile.id); }
    setCreateOpen(false); setName(""); onToast(`${title} создан`);
  };
  const activateProfile = (profile: SavedProfile) => { setActiveId(profile.id); if (nativeBridge.available) void nativeBridge.request("connectionProfile.select", { id: profile.id }); };
  const openEditor = (profile: SavedProfile) => { setEditingId(profile.id); setDraft({ ...profile, configIds: [...profile.configIds] }); setEditOpen(true); };
  const saveNativeProfile = (profile: SavedProfile) => { const colorArgb = Number.parseInt(`ff${profile.color.replace("#", "")}`, 16) | 0; return nativeBridge.request("connectionProfile.save", { id: profile.id, name: profile.name.trim(), colorArgb, subscriptionId: profile.subscriptionId === "none" ? null : profile.subscriptionId, routingProfileId: profile.routingProfileId || null, defaultServerId: profile.defaultServerId || null, configIds: profile.configIds, iorTrafficMode: profile.iorTrafficMode || "standard" }); };
  const saveProfile = () => { if (!draft || !draft.name.trim()) return; if (nativeBridge.available) void saveNativeProfile(draft); else setProfiles(items => items.map(item => item.id === editingId ? { ...draft, name: draft.name.trim() } : item)); setEditOpen(false); setDraft(null); onToast("Профиль сохранён"); };
  const toggleConfig = (config: string) => { if (!draft) return; setDraft({ ...draft, configIds: draft.configIds.includes(config) ? draft.configIds.filter(item => item !== config) : [...draft.configIds, config] }); };
  const addConfig = () => { const value = newConfig.trim(); if (!value) return; if (!configOptions.includes(value)) setConfigOptions(items => [...items, value]); if (draft && !draft.configIds.includes(value)) setDraft({ ...draft, configIds: [...draft.configIds, value] }); setNewConfig(""); };
  const removeProfile = (profile: SavedProfile) => {
    const remaining = profiles.filter(item => item.id !== profile.id);
    if (nativeBridge.available) void nativeBridge.request("connectionProfile.delete", { id: profile.id }); else setProfiles(remaining);
    if (activeId === profile.id && remaining[0]) setActiveId(remaining[0].id);
    onToast(`${profile.name} удалён`);
  };
  return <><section className="profiles-page"><div className="profiles-intro"><div><span>Наборы настроек</span><h2>Профили подключения</h2><p>Каждый профиль хранит свои подписки, маршрутизацию и личные конфиги.</p></div><button className="primary" onClick={() => setCreateOpen(true)}>+ Создать профиль</button></div><div className="profile-cards">{profiles.map((profile, index) => <motion.article key={profile.id} className={profile.id === activeId ? "active" : ""} initial={{ opacity: 0, y: 9 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: index * .04 }} layout onClick={() => activateProfile(profile)} onKeyDown={event => { if (event.key === "Enter" || event.key === " ") activateProfile(profile); }} role="button" tabIndex={0} aria-pressed={profile.id === activeId}><motion.button className="delete-profile" onClick={event => { event.stopPropagation(); removeProfile(profile); }} initial={false} aria-label={`Удалить профиль ${profile.name}`}><Icon size={15}><path d="M4 7h16M9 7V4h6v3M7 7l1 14h8l1-14M10 11v6M14 11v6"/></Icon></motion.button><div className="profile-card-head"><i style={{ background: profile.color }}/><span><small>Профиль</small><strong>{profile.name}</strong></span>{profile.id === activeId && <em>Активен</em>}</div><div className="profile-parts"><span><Icon size={16}><path d="M4 7h16M4 12h16M4 17h10"/></Icon><small>Подписка</small><strong>{subscriptionName(profile.subscriptionId)}</strong></span><span><Icon size={16}><circle cx="6" cy="6" r="2"/><circle cx="18" cy="6" r="2"/><circle cx="12" cy="18" r="2"/><path d="M8 6h8M7 8l4 8m6-8-4 8"/></Icon><small>Маршрут</small><strong>{routeName(profile.routingProfileId)}</strong></span><span><Icon size={16}><path d="M7 3h7l4 4v14H7zM14 3v5h5"/></Icon><small>Конфиги</small><strong>{profile.configIds.length}</strong></span></div><div className="profile-card-actions"><button onClick={event => { event.stopPropagation(); openEditor(profile); }}>Настроить</button><span>{profile.id === activeId ? "Используется сейчас" : "Нажмите, чтобы выбрать"}</span></div></motion.article>)}</div></section><AnimatePresence>{createOpen && <motion.div className="modal-layer" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onMouseDown={() => setCreateOpen(false)}><motion.div className="modal profile-modal" initial={{ opacity: 0, scale: .95, y: 15 }} animate={{ opacity: 1, scale: 1, y: 0 }} exit={{ opacity: 0, scale: .96 }} onMouseDown={event => event.stopPropagation()}><div className="modal-head"><div><span>Новый набор</span><h2>Создать профиль</h2></div><button onClick={() => setCreateOpen(false)}>×</button></div><label className="profile-name-input"><span>Название</span><input value={name} onChange={event => setName(event.target.value)} placeholder="Например, Игры" autoFocus/></label><div className="template-choice"><button className={template === "full" ? "selected" : ""} onClick={() => setTemplate("full")}><strong>Готовый набор</strong><small>Nova Plus, умная маршрутизация и 2 конфига</small></button><button className={template === "clean" ? "selected" : ""} onClick={() => setTemplate("clean")}><strong>Чистый профиль</strong><small>Пустой набор для ручной настройки</small></button></div><div className="modal-actions"><button className="secondary" onClick={() => setCreateOpen(false)}>Отмена</button><button className="primary" onClick={createProfile}>Создать</button></div></motion.div></motion.div>}</AnimatePresence><AnimatePresence>{editOpen && draft && <><motion.div className="drawer-backdrop" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={() => { setEditOpen(false); setDraft(null); }}/><motion.aside className="profile-drawer" initial={{ x: "100%" }} animate={{ x: 0 }} exit={{ x: "100%" }} transition={{ type: "spring", stiffness: 380, damping: 38 }}><header><div><span>Профиль подключения</span><h2>{draft.name}</h2></div><button onClick={() => { setEditOpen(false); setDraft(null); }}>×</button></header><div className="drawer-scroll"><label className="drawer-field"><span>Название</span><input value={draft.name} onChange={event => setDraft({ ...draft, name: event.target.value })}/></label><div className="drawer-field"><span>Цвет профиля</span><div className="color-options">{["#b8f356", "#5ae4be", "#7194ff", "#a68cff", "#ff647b", "#ffb86c"].map(color => <button key={color} className={draft.color === color ? "selected" : ""} style={{ background: color }} onClick={() => setDraft({ ...draft, color })}/>)}</div></div><label className="drawer-field"><span>Подписка</span><AnimatedSelect className="profile-subscription-select" value={draft.subscriptionId} onChange={value => setDraft({ ...draft, subscriptionId: value })} options={subscriptionOptions.map(item => ({ value: item.id, label: item.name }))}/></label><label className="drawer-field"><span>Маршрутизация</span><AnimatedSelect className="profile-routing-select" value={draft.routingProfileId} onChange={value => setDraft({ ...draft, routingProfileId: value })} options={routingOptions.map((item: { id: string; name: string }) => ({ value: item.id, label: item.name }))}/></label><section className="drawer-configs"><div><span>Личные конфиги</span><em>{draft.configIds.length} выбрано</em></div>{configOptions.map(config => <button key={config} className={draft.configIds.includes(config) ? "selected" : ""} onClick={() => toggleConfig(config)}><span>{config}</span><b><Icon size={12}><path d="m5 12 4 4 10-10"/></Icon></b></button>)}<form onSubmit={event => { event.preventDefault(); addConfig(); }}><input value={newConfig} onChange={event => setNewConfig(event.target.value)} placeholder="Название нового конфига"/><button type="submit">Добавить</button></form></section></div><footer><button className="secondary" onClick={() => { setEditOpen(false); setDraft(null); }}>Отмена</button><button className="primary" disabled={!draft.name.trim()} onClick={saveProfile}>Сохранить</button></footer></motion.aside></>}</AnimatePresence></>;
}

type RouteKind = "proxy" | "direct" | "block";

type RoutingProfile = {
  id: string; name: string; source: "ior" | "happ" | "clash"; defaultAction: "proxy" | "direct"; unmappedRules?: number;
  routeOrder: RouteKind[]; proxySites: string[]; directSites: string[]; blockSites: string[];
  proxyIp: string[]; directIp: string[]; blockIp: string[]; raw?: string;
  proxyDomains?: string[]; directDomains?: string[]; blockDomains?: string[];
  proxyKeywords?: string[]; directKeywords?: string[]; blockKeywords?: string[];
};

const clashRouteTemplate = "rules:\n  - DOMAIN-SUFFIX,ru,DIRECT\n  - DOMAIN-SUFFIX,su,DIRECT\n  - DOMAIN-SUFFIX,xn--p1ai,DIRECT\n  - MATCH,PROXY\n";

function ClashRouteEditor({ profile, onChange, onClose, onSave, error, saving }: { profile: RoutingProfile; onChange: (profile: RoutingProfile) => void; onClose: () => void; onSave: () => void; error: string; saving: boolean }) {
  const dialog = useRef<HTMLElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.querySelector<HTMLInputElement>("input")?.focus();
    return () => previous?.focus();
  }, []);
  return createPortal(<div className="clash-route-layer">
    <motion.div className="drawer-backdrop" initial={{ opacity: 0 }} animate={{ opacity: 1 }} onClick={() => { if (!saving) onClose(); }}/>
    <motion.aside ref={dialog} className="profile-drawer route-editor-drawer" role="dialog" aria-modal="true" aria-label="Редактор маршрута Clash" initial={{ x: "105%" }} animate={{ x: 0 }} transition={{ type: "spring", stiffness: 330, damping: 34 }} onKeyDown={event => {
      if (event.key !== "Tab") return;
      const items = Array.from(dialog.current?.querySelectorAll<HTMLElement>("button:not(:disabled),input,textarea") || []);
      if (!items.length) return;
      const first = items[0], last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }}>
      <header><div><span>Локальный профиль Clash / Mihomo</span><h2>Маршрут Clash</h2></div><button disabled={saving} aria-label="Закрыть редактор" onClick={onClose}>×</button></header>
      <div className="drawer-scroll"><label className="drawer-field"><span>Название</span><input value={profile.name} onChange={event => onChange({ ...profile, name: event.target.value })}/></label>
        <label className="drawer-field"><span>YAML: правила, группы и провайдеры</span><textarea className="clash-yaml-editor" spellCheck={false} value={profile.raw || ""} onChange={event => onChange({ ...profile, raw: event.target.value })}/></label>
        <p>Правила выполняются сверху вниз. DIRECT — напрямую, REJECT — блокировка, PROXY — выбранный сервер. Можно указать имя группы, если её поддерживает выбранное ядро.</p>
        <p>Изменения сохраняются локально; обновление подписки их не заменит. Совместимость правил проверяется перед подключением.</p>
        {error && <p role="alert" className="import-error">{error}</p>}
      </div><footer><button disabled={saving} onClick={onClose}>Отмена</button><button className="primary" disabled={saving || !profile.name.trim() || !profile.raw?.trim()} onClick={onSave}>{saving ? "Сохраняем…" : "Сохранить"}</button></footer>
    </motion.aside></div>, document.body);
}

const emptyRoute = (name = "Новый маршрут"): RoutingProfile => ({ id: `route-${Date.now()}`, name, source: "ior", defaultAction: "proxy", routeOrder: ["block", "proxy", "direct"], proxySites: [], directSites: [], blockSites: [], proxyIp: [], directIp: [], blockIp: [] });
const defaultRoutes: RoutingProfile[] = [
  { ...emptyRoute("Умный маршрут"), id: "smart", proxySites: ["youtube.com", "discord.com", "instagram.com"], directSites: ["yandex.ru", "ozon.ru"], blockSites: ["ads.example"] },
  { ...emptyRoute("Корпоративный"), id: "work", proxySites: ["github.com", "slack.com"], directSites: ["intranet.local"], blockSites: [] },
  { ...emptyRoute("Happ Community"), id: "happ-community", source: "happ", raw: "happ://routing/community", proxySites: ["youtube.com"], directSites: ["ru"], blockSites: ["ads"] }
];
const hydrateRoute = (item: any, index = 0): RoutingProfile => ({ id: String(item?.id || `route-${Date.now()}-${index}`), name: String(item?.name || `Маршрут ${index + 1}`), source: String(item?.source).toLowerCase() === "clash" ? "clash" : String(item?.source).toLowerCase() === "happ" ? "happ" : "ior", defaultAction: (item?.globalProxy ?? item?.defaultAction !== "direct") ? "proxy" : "direct", routeOrder: ["block", "proxy", "direct"], proxySites: Array.isArray(item?.proxySites) ? item.proxySites.map(String) : [], directSites: Array.isArray(item?.directSites) ? item.directSites.map(String) : [], blockSites: Array.isArray(item?.blockSites) ? item.blockSites.map(String) : [], proxyIp: Array.isArray(item?.proxyIp) ? item.proxyIp.map(String) : [], directIp: Array.isArray(item?.directIp) ? item.directIp.map(String) : [], blockIp: Array.isArray(item?.blockIp) ? item.blockIp.map(String) : [], proxyDomains: item?.proxyDomains || [], directDomains: item?.directDomains || [], blockDomains: item?.blockDomains || [], proxyKeywords: item?.proxyKeywords || [], directKeywords: item?.directKeywords || [], blockKeywords: item?.blockKeywords || [], unmappedRules: Array.isArray(item?.clashUnmappedRules) ? item.clashUnmappedRules.length : 0, raw: typeof item?.originalClashYaml === "string" ? item.originalClashYaml : typeof item?.raw === "string" ? item.raw : undefined });

function Routing() {
  const nativeState = useNativeSnapshot();
  const [profiles, setProfiles] = useState<RoutingProfile[]>(() => { try { const value = JSON.parse(localStorage.getItem("ior-routing-profiles") || "null"); return Array.isArray(value) && value.length ? value.map(hydrateRoute) : defaultRoutes; } catch { return defaultRoutes; } });
  const [selectedProfile, setSelectedProfile] = useState(() => localStorage.getItem("ior-active-routing-profile") || "smart");
  const [editor, setEditor] = useState<RoutingProfile | null>(null);
  const [createRouteOpen, setCreateRouteOpen] = useState(false);
  const [editorError, setEditorError] = useState("");
  const [editorSaving, setEditorSaving] = useState(false);
  const [ruleDrafts, setRuleDrafts] = useState<Record<string, string>>({});
  const [importOpen, setImportOpen] = useState(false);
  const [importValue, setImportValue] = useState("");
  const [importError, setImportError] = useState("");
  const [contextMenu, setContextMenu] = useState<{ profile: RoutingProfile; x: number; y: number } | null>(null);
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState("");
  const [shareValue, setShareValue] = useState("");
  useEffect(() => { if (!nativeBridge.available) localStorage.setItem("ior-routing-profiles", JSON.stringify(profiles)); }, [profiles]);
  useEffect(() => { if (!nativeBridge.available) localStorage.setItem("ior-active-routing-profile", selectedProfile); }, [selectedProfile]);
  useEffect(() => { if (!nativeState) return; setProfiles(nativeState.routes.map(hydrateRoute)); if (nativeState.activeRoutingProfileId) setSelectedProfile(nativeState.activeRoutingProfileId); }, [nativeState]);
  useEffect(() => { if (nativeState && selectedProfile && selectedProfile !== nativeState.activeRoutingProfileId && nativeState.routes.some(route => String(route.id) === selectedProfile)) void nativeBridge.request("route.select", { id: selectedProfile }).catch(error => { window.alert(error instanceof Error ? error.message : String(error)); setSelectedProfile(nativeState.activeRoutingProfileId || ""); }); }, [selectedProfile, nativeState?.activeRoutingProfileId]);
  useEffect(() => { if (!nativeState) return; const changed = profiles.find(profile => nativeState.routes.some(route => String(route.id) === profile.id && String(route.name) !== profile.name)); if (changed) void nativeBridge.request("route.rename", { id: changed.id, name: changed.name }); }, [profiles]);
  useEffect(() => { const close = (event: KeyboardEvent) => { if (event.key === "Escape") { setContextMenu(null); setEditor(null); setCreateRouteOpen(false); setImportOpen(false); setShareValue(""); } }; window.addEventListener("keydown", close); return () => window.removeEventListener("keydown", close); }, []);
  useEffect(() => { if (!contextMenu) return; const close = () => setContextMenu(null); window.addEventListener("pointerdown", close); return () => window.removeEventListener("pointerdown", close); }, [contextMenu]);
  const countRules = (profile: RoutingProfile) => profile.proxySites.length + profile.directSites.length + profile.blockSites.length + profile.proxyIp.length + profile.directIp.length + profile.blockIp.length;
  const openContext = (event: React.MouseEvent, profile: RoutingProfile) => { event.preventDefault(); event.stopPropagation(); setRenameValue(profile.name); setRenaming(false); setContextMenu({ profile, x: Math.max(12, Math.min(event.clientX, window.innerWidth - 230)), y: Math.max(12, Math.min(event.clientY, window.innerHeight - 245)) }); };
  const saveProfiles = (next: RoutingProfile[]) => setProfiles(next);
  const removeProfile = (profile: RoutingProfile) => {
    const index = profiles.findIndex(item => item.id === profile.id); let next = profiles.filter(item => item.id !== profile.id);
    if (!next.length) next = [{ ...emptyRoute("Без маршрутизации"), id: `route-${Date.now()}` }];
    if (selectedProfile === profile.id) setSelectedProfile(next[Math.min(index, next.length - 1)].id);
    if (nativeBridge.available) void nativeBridge.request("route.delete", { id: profile.id }); else saveProfiles(next); setContextMenu(null);
  };
  const encodedShare = (profile: RoutingProfile) => {
    if (profile.source === "clash" && profile.raw) return profile.raw;
    if (profile.source === "happ" && profile.raw) return profile.raw;
    const payload = JSON.stringify({ version: 1, name: profile.name, globalProxy: profile.defaultAction === "proxy", routeOrder: profile.routeOrder, proxySites: profile.proxySites, directSites: profile.directSites, blockSites: profile.blockSites, proxyIp: profile.proxyIp, directIp: profile.directIp, blockIp: profile.blockIp });
    return `ior-route://${btoa(unescape(encodeURIComponent(payload)))}`;
  };
  const share = async (profile: RoutingProfile) => { const value = nativeBridge.available ? await nativeBridge.request<string>("route.share", { id: profile.id }) : encodedShare(profile); try { await navigator.clipboard.writeText(value); setContextMenu(null); } catch { setShareValue(value); setContextMenu(null); } };
  const normalizeProfile = (data: any, raw: string): RoutingProfile | null => {
    if (!data || typeof data !== "object") return null;
    const source = data.version === 1 || data.source === "ior" ? "ior" : "happ";
    const list = (camel: string, pascal: string) => Array.isArray(data[camel]) ? data[camel].map(String) : Array.isArray(data[pascal]) ? data[pascal].map(String) : [];
    return { id: `route-${Date.now()}`, name: String(data.name || data.Name || (source === "ior" ? "Импортированный маршрут" : "Happ профиль")), source, defaultAction: data.globalProxy ?? data.GlobalProxy ? "proxy" : "direct", routeOrder: ["block", "proxy", "direct"], proxySites: list("proxySites", "ProxySites"), directSites: list("directSites", "DirectSites"), blockSites: list("blockSites", "BlockSites"), proxyIp: list("proxyIp", "ProxyIp"), directIp: list("directIp", "DirectIp"), blockIp: list("blockIp", "BlockIp"), raw: source === "happ" ? raw : undefined };
  };
  const importProfile = async () => {
    const raw = importValue.trim(); setImportError(""); if (!raw) return;
    try {
      if (nativeBridge.available) { await nativeBridge.request("route.import", { text: raw }); setImportValue(""); setImportOpen(false); return; }
      let profile: RoutingProfile | null = null;
      if (raw.startsWith("ior-route://")) profile = normalizeProfile(JSON.parse(decodeURIComponent(escape(atob(raw.slice(12))))), raw);
      else if (raw.startsWith("{") || raw.startsWith("[")) profile = normalizeProfile(JSON.parse(raw), raw);
      else if (/^(happ|https?):\/\//i.test(raw)) profile = { ...emptyRoute("Happ профиль"), source: "happ", raw };
      if (!profile) throw new Error();
      setProfiles(items => [...items, profile!]); setSelectedProfile(profile.id); setImportValue(""); setImportOpen(false);
    } catch (error) { setImportError(error instanceof Error ? error.message : "Не удалось распознать профиль."); }
  };
  const saveEditor = async () => {
    if (!editor || !editor.name.trim() || editorSaving) return;
    if (nativeState?.connected && !window.confirm("Сохранение выберет этот маршрут и переподключит VPN. Продолжить?")) return;
    setEditorError(""); setEditorSaving(true);
    try {
      if (nativeBridge.available) await nativeBridge.request("route.save", editor.source === "clash"
        ? { id: editor.id, name: editor.name.trim(), source: "clash", originalClashYaml: editor.raw }
        : { id: editor.id, name: editor.name.trim(), source: "ior", globalProxy: editor.defaultAction === "proxy", routeOrder: editor.routeOrder, proxySites: editor.proxySites, directSites: editor.directSites, blockSites: editor.blockSites, proxyIp: editor.proxyIp, directIp: editor.directIp, blockIp: editor.blockIp, proxyDomains: editor.proxyDomains || [], directDomains: editor.directDomains || [], blockDomains: editor.blockDomains || [], proxyKeywords: editor.proxyKeywords || [], directKeywords: editor.directKeywords || [], blockKeywords: editor.blockKeywords || [], links: [] });
      else setProfiles(items => items.some(item => item.id === editor.id) ? items.map(item => item.id === editor.id ? { ...editor, name: editor.name.trim() } : item) : [...items, { ...editor, name: editor.name.trim() }]);
      setSelectedProfile(editor.id); setEditor(null);
    } catch (error) { setEditorError(error instanceof Error ? error.message : String(error)); }
    finally { setEditorSaving(false); }
  };
  const ruleKeys: { kind: RouteKind; title: string; text: string }[] = [{ kind: "proxy", title: "Через VPN", text: "Отправлять через выбранный сервер" }, { kind: "direct", title: "Напрямую", text: "Не использовать VPN для этих адресов" }, { kind: "block", title: "Блокировать", text: "Запрещать соединение" }];
  const addRule = (kind: RouteKind, ip: boolean) => { if (!editor) return; const key = `${kind}-${ip ? "ip" : "sites"}`; const value = (ruleDrafts[key] || "").trim().replace(/^https?:\/\//, "").replace(/\/$/, ""); if (!value) return; const field = `${kind}${ip ? "Ip" : "Sites"}` as keyof RoutingProfile; const current = editor[field] as string[]; if (current.includes(value)) return; setEditor({ ...editor, [field]: [...current, value] }); setRuleDrafts(items => ({ ...items, [key]: "" })); };
  const removeRule = (kind: RouteKind, ip: boolean, value: string) => { if (!editor) return; const field = `${kind}${ip ? "Ip" : "Sites"}` as keyof RoutingProfile; setEditor({ ...editor, [field]: (editor[field] as string[]).filter(item => item !== value) }); };
  return <div className="routing-page routing-list-page"><section className="routing-toolbar"><div><span>Профили маршрутизации</span><h2>Куда направлять трафик</h2><p>Clash применяет правила сверху вниз; ior — по порядку выбранных категорий.</p></div><div><button onClick={() => { setEditorError(""); setCreateRouteOpen(true); }}>+ Создать профиль</button><button className="primary" onClick={() => { setImportError(""); setImportOpen(true); }}>Импортировать</button></div></section><section className="routing-profile-list">{profiles.map(profile => <motion.article layout key={profile.id} className={`routing-profile-card ${selectedProfile === profile.id ? "active" : ""}`} onClick={() => { if (!nativeBridge.available && profile.unmappedRules) { window.alert(`В профиле есть ${profile.unmappedRules} неподдерживаемых правил Clash.`); return; } setSelectedProfile(profile.id); }} onContextMenu={event => openContext(event, profile)} whileHover={{ y: -2 }}><i className="route-accent"/><div className="route-card-copy"><div><strong>{profile.name}</strong><span className={`route-source-badge ${profile.source}`}>{profile.source === "ior" ? "ior · редактируемый" : profile.source === "clash" ? "Clash · редактируемый" : "Happ · только чтение"}</span></div><small>{countRules(profile)} правил{profile.unmappedRules ? ` · ${profile.unmappedRules} пока не поддерживаются` : ""} · по умолчанию {profile.defaultAction === "proxy" ? "через VPN" : "напрямую"}</small></div><div className="route-counts"><span><b>{profile.proxySites.length + profile.proxyIp.length}</b> VPN</span><span><b>{profile.directSites.length + profile.directIp.length}</b> напрямую</span><span><b>{profile.blockSites.length + profile.blockIp.length}</b> блок</span></div>{selectedProfile === profile.id && <motion.em layoutId="active-route-pill">Активен</motion.em>}<button className="route-more" onClick={event => openContext(event, profile)} aria-label="Действия">•••</button></motion.article>)}</section>
  {createRouteOpen && createPortal(<div className="modal-layer" style={{ zIndex: 11000 }} onMouseDown={() => setCreateRouteOpen(false)}><div className="modal routing-modal" role="dialog" aria-modal="true" aria-label="Формат маршрутизации" onMouseDown={event => event.stopPropagation()}><div className="modal-head"><h2>Создать маршрут</h2><button aria-label="Закрыть" onClick={() => setCreateRouteOpen(false)}>×</button></div><p>Выберите формат маршрутизации.</p><div className="modal-actions"><button autoFocus onClick={() => { setEditor(emptyRoute()); setCreateRouteOpen(false); }}>ior · списки адресов</button><button className="primary" onClick={() => { setEditor({ ...emptyRoute("Новый маршрут Clash"), source: "clash", raw: clashRouteTemplate }); setCreateRouteOpen(false); }}>Clash / Mihomo · YAML</button></div></div></div>, document.body)}
  {editor?.source === "clash" && <ClashRouteEditor profile={editor} onChange={setEditor} onClose={() => setEditor(null)} onSave={saveEditor} error={editorError} saving={editorSaving}/>}
  <AnimatePresence>{contextMenu && <motion.div className="profile-context-menu route-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} initial={{ opacity: 0, scale: .76, y: 7, filter: "blur(8px)" }} animate={{ opacity: 1, scale: 1, y: 0, filter: "blur(0)" }} exit={{ opacity: 0, scale: .88, filter: "blur(4px)" }} transition={{ type: "spring", stiffness: 520, damping: 34 }} onPointerDown={event => event.stopPropagation()}>{renaming ? <form className="context-rename" onSubmit={event => { event.preventDefault(); if (!renameValue.trim()) return; setProfiles(items => items.map(item => item.id === contextMenu.profile.id ? { ...item, name: renameValue.trim() } : item)); setContextMenu(null); }}><label>Новое название</label><input autoFocus value={renameValue} onChange={event => setRenameValue(event.target.value)}/><div><button type="button" onClick={() => setRenaming(false)}>Отмена</button><button>Сохранить</button></div></form> : <><div className="context-title"><small>{contextMenu.profile.source === "ior" ? "Профиль ior" : contextMenu.profile.source === "clash" ? "Профиль Clash" : "Профиль Happ"}</small><strong>{contextMenu.profile.name}</strong></div><button onClick={() => setRenaming(true)}>Переименовать</button><button onClick={() => share(contextMenu.profile)}>Поделиться</button>{contextMenu.profile.source !== "happ" && <button onClick={() => { setEditorError(""); setEditor({ ...contextMenu.profile }); setContextMenu(null); }}>Изменить</button>}{contextMenu.profile.source === "clash" && <button onClick={() => { if (contextMenu.profile.unmappedRules && !window.confirm(`${contextMenu.profile.unmappedRules} правил не поддерживаются и не войдут в локальную копию. Продолжить?`)) return; if (nativeBridge.available) void nativeBridge.request("route.convert", { id: contextMenu.profile.id }).then(() => setContextMenu(null)); else { setProfiles(items => [...items, { ...contextMenu.profile, id: `route-${Date.now()}`, name: `${contextMenu.profile.name} (ior)`, source: "ior", unmappedRules: 0, raw: undefined }]); setContextMenu(null); } }}>Конвертировать в ior</button>}<i/><button className="danger" onClick={() => removeProfile(contextMenu.profile)}>Удалить</button></>}</motion.div>}</AnimatePresence>
  <AnimatePresence>{editor && editor.source !== "clash" && <><motion.div className="drawer-backdrop" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={() => setEditor(null)}/><motion.aside className="profile-drawer route-editor-drawer" initial={{ x: "105%" }} animate={{ x: 0 }} exit={{ x: "105%" }} transition={{ type: "spring", stiffness: 330, damping: 34 }}><header><div><span>Локальный профиль ior</span><h2>{profiles.some(item => item.id === editor.id) ? "Изменить маршрут" : "Новый маршрут"}</h2></div><button onClick={() => setEditor(null)}>×</button></header><div className="drawer-scroll"><label className="drawer-field"><span>Название</span><input value={editor.name} onChange={event => setEditor({ ...editor, name: event.target.value })}/></label><label className="drawer-field"><span>Действие по умолчанию</span><AnimatedSelect className="route-default-select" value={editor.defaultAction} onChange={value => setEditor({ ...editor, defaultAction: value as "proxy" | "direct" })} options={[{ value: "proxy", label: "Весь остальной трафик через VPN" }, { value: "direct", label: "Весь остальной трафик напрямую" }]}/><small>Это финальное правило для адресов, которые не совпали с правилами ниже.</small></label><div className="route-order-note"><strong>Порядок обработки</strong><span>1. Блокировать</span><span>2. Через VPN</span><span>3. Напрямую</span></div>{ruleKeys.map(meta => <section className={`route-rule-editor ${meta.kind}`} key={meta.kind}><header><div><strong>{meta.title}</strong><small>{meta.text}</small></div><em>{(editor[`${meta.kind}Sites` as keyof RoutingProfile] as string[]).length + (editor[`${meta.kind}Ip` as keyof RoutingProfile] as string[]).length}</em></header>{([false, true] as const).map(ip => { const key = `${meta.kind}-${ip ? "ip" : "sites"}`; const field = `${meta.kind}${ip ? "Ip" : "Sites"}` as keyof RoutingProfile; const values = editor[field] as string[]; return <div className="route-rule-group" key={key}><label>{ip ? "IP или CIDR" : "Домены и сайты"}</label><form onSubmit={event => { event.preventDefault(); addRule(meta.kind, ip); }}><input value={ruleDrafts[key] || ""} onChange={event => setRuleDrafts(items => ({ ...items, [key]: event.target.value }))} placeholder={ip ? "1.2.3.4 или 10.0.0.0/8" : "example.com"}/><button>+</button></form><div>{values.map(value => <motion.span layout key={value}>{value}<button onClick={() => removeRule(meta.kind, ip, value)}>×</button></motion.span>)}</div></div>; })}</section>)}</div><footer>{editorError && <p role="alert" className="import-error">{editorError}</p>}<button onClick={() => setEditor(null)}>Отмена</button><button className="primary" disabled={!editor.name.trim() || editorSaving} onClick={saveEditor}>Сохранить</button></footer></motion.aside></>}</AnimatePresence>
  <AnimatePresence>{importOpen && <motion.div className="modal-layer" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onMouseDown={() => setImportOpen(false)}><motion.div className="modal routing-modal" initial={{ opacity: 0, scale: .92, y: 15 }} animate={{ opacity: 1, scale: 1, y: 0 }} exit={{ opacity: 0, scale: .95 }} onMouseDown={event => event.stopPropagation()}><div className="modal-head"><div><span>Профиль маршрутизации</span><h2>Импортировать</h2></div><button onClick={() => setImportOpen(false)}>×</button></div><label className="profile-name-input"><span>Happ, Clash/Mihomo YAML, clash://, JSON или ior-route://</span><textarea value={importValue} onChange={event => setImportValue(event.target.value)} placeholder="Вставьте ссылку, YAML или JSON" autoFocus/></label>{importError && <p className="import-error">{importError}</p>}<div className="modal-actions"><button className="secondary" onClick={() => setImportOpen(false)}>Отмена</button><button className="primary" disabled={!importValue.trim()} onClick={importProfile}>Импортировать</button></div></motion.div></motion.div>}</AnimatePresence>
  <AnimatePresence>{shareValue && <motion.div className="modal-layer" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onMouseDown={() => setShareValue("")}><motion.div className="modal routing-modal" onMouseDown={event => event.stopPropagation()} initial={{ scale: .94, opacity: 0 }} animate={{ scale: 1, opacity: 1 }}><div className="modal-head"><div><span>Буфер обмена недоступен</span><h2>Поделиться профилем</h2></div><button onClick={() => setShareValue("")}>×</button></div><textarea className="share-text" readOnly value={shareValue} onFocus={event => event.currentTarget.select()}/><div className="modal-actions"><button onClick={() => { let json = shareValue; if (shareValue.startsWith("ior-route://")) { try { json = JSON.stringify(JSON.parse(decodeURIComponent(escape(atob(shareValue.slice(12))))), null, 2); } catch {} } else if (!shareValue.trim().startsWith("{")) json = JSON.stringify({ source: "Happ", link: shareValue }, null, 2); const blob = new Blob([json], { type: "application/json" }); const link = document.createElement("a"); link.href = URL.createObjectURL(blob); link.download = "ior-route.json"; link.click(); URL.revokeObjectURL(link.href); }}>Сохранить JSON</button><button className="primary" onClick={() => setShareValue("")}>Готово</button></div></motion.div></motion.div>}</AnimatePresence></div>;
}

function ConfigPage({ onToast }: { onToast: (message: string) => void }) {
  const nativeState = useNativeSnapshot();
  const [profile, setProfile] = useState("Amsterdam 01");
  const [validated, setValidated] = useState<boolean | null>(null);
  const [config, setConfig] = useState(() => JSON.stringify({ status: "Откройте настоящее приложение ior для генерации конфигурации" }, null, 2));
  useEffect(() => { if (!nativeBridge.available) return; nativeBridge.request<string>("config.get").then(setConfig).catch(error => setConfig(error.message)); }, [nativeState?.selectedServerId, nativeState?.activeRoutingProfileId]);
  const download = () => { const url = URL.createObjectURL(new Blob([config], { type: "application/json" })); const link = document.createElement("a"); link.href = url; link.download = "ior-config.json"; link.click(); URL.revokeObjectURL(url); onToast("Конфигурация сохранена"); };
  const profileOptions = nativeState?.servers.map(item => ({ value: item.id, label: item.name })) || [{ value: profile, label: profile }];
  const result = nativeBridge.available ? nativeState?.configValidation?.valid : validated;
  const status = result === true ? "Конфигурация корректна" : result === false ? "Проверка не пройдена" : nativeState?.settings.autoValidateConfig === false ? "Автопроверка отключена" : "Проверяем конфигурацию…";
  return <div className="config-page"><section className="content-card config-toolbar"><div><span>Генератор sing-box</span><h3>Итоговая конфигурация</h3><p>Предпросмотр создаётся из выбранного сервера, сетевых параметров и правил маршрутизации.</p></div><div><AnimatedSelect className="config-profile-select" value={nativeState?.selectedServerId || profile} onChange={value => { setProfile(value); setValidated(null); if (nativeBridge.available) void nativeBridge.request("server.select", { id: value }); }} options={profileOptions}/><button onClick={async () => { const ok = nativeBridge.available ? await nativeBridge.request<boolean>("config.validate") : true; setValidated(ok); onToast(ok ? "Конфигурация прошла проверку" : "Проверка не пройдена"); }}>Проверить</button><button className="primary" onClick={download}>Сохранить JSON</button></div></section><section className="content-card config-editor"><header><div><i className={result === true ? "ok" : ""}/><span>{status}</span></div><button onClick={() => navigator.clipboard?.writeText(config).then(() => onToast("Конфигурация скопирована")).catch(() => onToast("Выделите текст и скопируйте вручную"))}>Копировать</button></header><pre>{config}</pre></section></div>;
}

function Logs({ onToast }: { onToast: (message: string) => void }) {
  const nativeState = useNativeSnapshot();
  const initialLogs = ["[12:44:02] [INFO] ior ready", "[12:44:03] [INFO] sing-box core detected", "[12:44:03] [INFO] loaded 6 profiles", "[12:44:04] [DEBUG] latency check completed: 34 ms", "[12:44:05] [WARN] system proxy is disabled"];
  const [logs, setLogs] = useState(initialLogs);
  const [query, setQuery] = useState("");
  const [level, setLevel] = useState("ALL");
  const [paused, setPaused] = useState(false);
  useEffect(() => {
    if (!nativeState || paused) return;
    const probes = nativeState.tunReadiness;
    const results = probes ? (["https", "dns", "udp"] as const).map(key => {
      const probe = probes[key];
      const status = probe.status === "ready" ? "готово" : probe.status === "error" ? "ошибка проверки" : "не проверено";
      return `[TUN readiness] ${key.toUpperCase()}: ${status} · code=${probe.code} · durationMs=${probe.durationMs}`;
    }) : [];
    setLogs([...nativeState.logs, ...results]);
  }, [nativeState, paused]);
  const filtered = logs.filter(line => (level === "ALL" || line.includes(`[${level}]`)) && line.toLowerCase().includes(query.toLowerCase()));
  const exportLogs = async () => { if (nativeBridge.available) { try { const result = await nativeBridge.request<string | null>("diagnostics.export"); if (result) onToast(result); } catch { onToast("Не удалось экспортировать диагностику"); } return; } const url = URL.createObjectURL(new Blob([logs.join("\n")], { type: "text/plain" })); const link = document.createElement("a"); link.href = url; link.download = "ior.log"; link.click(); URL.revokeObjectURL(url); onToast("Журнал экспортирован"); };
  const diagnostic = nativeState?.diagnostics;
  return <section className="content-card logs logs-page"><div className="card-title"><div><span>sing-box</span><h3>Текущий сеанс</h3></div><div className="log-actions"><button className={paused ? "active" : ""} onClick={() => setPaused(value => !value)}>{paused ? "Продолжить" : "Пауза"}</button><button onClick={exportLogs}>Экспорт</button><button onClick={() => { setLogs([]); if (nativeBridge.available) void nativeBridge.request("logs.clear"); onToast("Журнал очищен"); }}>Очистить</button></div></div>{diagnostic && <div className="log-diagnostics" aria-label="Диагностика нагрузки"><span>ior <b>{diagnostic.appCpuPercent?.toFixed(1) ?? "—"}% CPU</b></span><span>Ядро <b>{diagnostic.engineCpuPercent?.toFixed(1) ?? "—"}% CPU</b></span><span title="Считаются DNS-пакеты, попавшие в INFO-журнал ядра">DNS TUN* <b>{diagnostic.tunDnsLinesPerSecond.toFixed(0)}/с</b></span><span>Журнал <b>{diagnostic.logLinesPerSecond.toFixed(0)}/с</b></span><span>{diagnostic.mode} · системный прокси {diagnostic.systemProxy ? "вкл." : "выкл."}</span></div>}<div className="log-filters"><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Поиск по журналу"/><AnimatedSelect className="log-level-select" value={level} onChange={setLevel} options={["ALL", "INFO", "DEBUG", "WARN", "ERROR"].map(item => ({ value: item, label: item }))}/><span>{filtered.length} строк</span></div><pre>{filtered.join("\n") || "Нет записей по выбранному фильтру"}</pre></section>;
}

function SubscriptionRefreshRow({ id, name, globalEnabled, enabled, intervalHours, nextRefreshUtc, onEnabled, onInterval }: { id: string; name: string; globalEnabled: boolean; enabled: boolean; intervalHours: number; nextRefreshUtc?: string; onEnabled: (enabled: boolean) => void; onInterval: (hours: number) => void }) {
  const next = nextRefreshUtc ? new Date(nextRefreshUtc) : new Date(Date.now() + intervalHours * 60 * 60 * 1000);
  const description = `Следующее через ${intervalHours} ч · ${next.toLocaleDateString("ru-RU", { day: "numeric", month: "short" })}, ${next.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" })}`;
  return <div className={`refresh-row ${!globalEnabled || !enabled ? "disabled" : ""}`}><span><i/><strong>{name}</strong><small>{!globalEnabled ? "Отключено в общих настройках" : !enabled ? "Автообновление отключено" : description}</small></span><div className="refresh-controls"><motion.button className={`toggle ${enabled ? "active" : ""}`} disabled={!globalEnabled} onClick={() => onEnabled(!enabled)} whileTap={{ scale: .93 }} aria-pressed={enabled}><motion.span animate={{ x: enabled ? 19 : 0 }} transition={{ type: "spring", stiffness: 520, damping: 38 }}/></motion.button><label><small>Интервал</small><AnimatedSelect className={`refresh-interval-select-${id}`} disabled={!globalEnabled || !enabled} value={intervalHours} onChange={value => onInterval(Number(value))} options={[...new Set([1, 3, 6, 12, 24, 48, intervalHours])].sort((a, b) => a - b).map(hours => ({ value: String(hours), label: `${hours} ч` }))}/></label></div></div>;
}

function Settings({ navigationPosition, onNavigationPosition }: { navigationPosition: "left" | "top"; onNavigationPosition: (position: "left" | "top") => void }) {
  const nativeState = useNativeSnapshot();
  const nativeSettings = nativeState?.settings || {};
  const updateNative = (values: Record<string, unknown>) => { if (nativeBridge.available) void nativeBridge.request("settings.update", values); };
  const exportBackup = async () => { if (!nativeBridge.available) return; const json = await nativeBridge.request<string>("backup.export"); const link = document.createElement("a"); link.href = URL.createObjectURL(new Blob([json], { type: "application/json" })); link.download = `ior-backup-${new Date().toISOString().slice(0, 10)}.json`; link.click(); URL.revokeObjectURL(link.href); };
  const importBackup = () => { if (!nativeBridge.available) return; const input = document.createElement("input"); input.type = "file"; input.accept = ".json,application/json"; input.onchange = async () => { const file = input.files?.[0]; if (!file || !window.confirm("Восстановить состояние ior из выбранной копии? Текущие данные будут сохранены в файл .before-import.")) return; try { await nativeBridge.request("backup.import", { json: await file.text() }); } catch (error) { window.alert(error instanceof Error ? error.message : String(error)); } }; input.click(); };
  const [themeId, setThemeId] = useState(() => nativeBridge.available ? "graphite" : window.localStorage.getItem("ior-theme") || "graphite");
  const selectTheme = (id: string) => {
    setThemeId(id);
    const theme = themes.find(item => item.id === id);
    if (theme) applyTheme(theme);
    const names: Record<string, string> = { "absolute-black": "darker", graphite: "graphite", midnight: "midnight", crimson: "redNeon", emerald: "emerald", violet: "violet", amber: "amber", "pure-light": "pureLight", ice: "iceBlue", rose: "whiteRed", mint: "mintGlass", lavender: "lightLavender", peach: "peachCream", sky: "skyGradient" };
    if (nativeBridge.available) updateNative({ theme: names[id] || "graphite" });
  };
  const [settingsTab, setSettingsTab] = useState<"general" | "connection" | "dns" | "advanced" | "appearance">("general");
  const [refreshIntervals, setRefreshIntervals] = useState<Record<string, number>>(() => ({ nova: Number(window.localStorage.getItem("ior-refresh-nova") || 6), work: Number(window.localStorage.getItem("ior-refresh-work") || 12) }));
  const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => window.localStorage.getItem("ior-refresh-global") !== "false");
  const [refreshEnabled, setRefreshEnabled] = useState<Record<string, boolean>>(() => ({ nova: window.localStorage.getItem("ior-refresh-enabled-nova") !== "false", work: window.localStorage.getItem("ior-refresh-enabled-work") !== "false" }));
  useEffect(() => {
    if (nativeBridge.available) return;
    const theme = themes.find(item => item.id === themeId) ?? themes[1];
    applyTheme(theme);
    window.localStorage.setItem("ior-theme", theme.id);
  }, [themeId]);
  useEffect(() => {
    if (!nativeState) return;
    const raw = String(nativeSettings.theme || "graphite").replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase();
    const aliases: Record<string, string> = { dark: "graphite", darker: "absolute-black", "red-neon": "crimson", "pure-light": "pure-light", "ice-blue": "ice", "white-red": "rose", "mint-glass": "mint", "light-lavender": "lavender", "peach-cream": "peach", "sky-gradient": "sky" };
    setThemeId(aliases[raw] || raw);
    setAutoRefreshEnabled(Boolean(nativeSettings.autoRefreshSubscriptions));
    const intervals: Record<string, number> = {}; const enabled: Record<string, boolean> = {};
    nativeState.subscriptions.forEach(item => { intervals[String(item.id)] = Number(item.refreshIntervalHours || 12); enabled[String(item.id)] = item.autoRefreshEnabled !== false; });
    setRefreshIntervals(intervals); setRefreshEnabled(enabled);
  }, [nativeState]);
  useEffect(() => {
    if (nativeBridge.available) return;
    window.localStorage.setItem("ior-refresh-nova", String(refreshIntervals.nova));
    window.localStorage.setItem("ior-refresh-work", String(refreshIntervals.work));
    Object.entries(refreshIntervals).forEach(([id, hours]) => {
      if (!window.localStorage.getItem(`ior-refresh-last-${id}`)) window.localStorage.setItem(`ior-refresh-last-${id}`, String(Date.now()));
      window.localStorage.setItem(`ior-refresh-next-${id}`, String(Date.now() + hours * 60 * 60 * 1000));
    });
  }, [refreshIntervals]);
  useEffect(() => { if (!nativeBridge.available) window.localStorage.setItem("ior-refresh-global", String(autoRefreshEnabled)); }, [autoRefreshEnabled]);
  useEffect(() => { if (!nativeBridge.available) Object.entries(refreshEnabled).forEach(([id, enabled]) => window.localStorage.setItem(`ior-refresh-enabled-${id}`, String(enabled))); }, [refreshEnabled]);
  const tabs = [{ id: "general", label: "Общие", icon: <path d="M5 4h14v16H5zM8 8h8M8 12h5"/> }, { id: "connection", label: "Подключение", icon: <><circle cx="6" cy="6" r="2"/><circle cx="18" cy="6" r="2"/><circle cx="12" cy="18" r="2"/><path d="M8 6h8M7 8l4 8m6-8-4 8"/></> }, { id: "dns", label: "DNS", icon: <><path d="M4 6h16v5H4zM4 14h16v5H4z"/><path d="M7 8.5h.01M7 16.5h.01"/></> }, { id: "advanced", label: "Дополнительно", icon: <><path d="M4 7h10M18 7h2M4 17h2M10 17h10"/><circle cx="16" cy="7" r="2"/><circle cx="8" cy="17" r="2"/></> }, { id: "appearance", label: "Оформление", icon: <><circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 0 0 16Z"/></> }] as const;
  return <div className="settings-shell"><div className="settings-tabs" role="tablist" aria-label="Разделы настроек">{tabs.map(tab => <button key={tab.id} role="tab" aria-selected={settingsTab === tab.id} className={settingsTab === tab.id ? "active" : ""} onClick={() => setSettingsTab(tab.id)}>{settingsTab === tab.id && <motion.i layoutId="settings-tab-pill" transition={{ type: "spring", duration: .25, bounce: .05 }}/>}<Icon size={17}>{tab.icon}</Icon><span>{tab.label}</span></button>)}</div><AnimatePresence mode="wait"><motion.div key={settingsTab} className="settings-panel" role="tabpanel" initial={{ opacity: 0, x: 10 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -8 }} transition={{ duration: .17 }}>
{settingsTab === "advanced" && <section className="content-card settings-section"><div className="card-title"><div><span>Тестирование и нагрузка</span><h3>Дополнительно</h3></div></div><Setting title="Экспериментальные сочетания ior-core" text="Остальные протоколы ior-core доступны для тестирования при включении. VLESS Reality и Hysteria2 в Proxy и TUN доступны всегда; TUN требует Windows 11."><Toggle value={Boolean(nativeSettings.experimentalCoreTesting)} onChange={value => updateNative({ experimentalCoreTesting: value })}/></Setting><div className="experimental-matrix"><p>Статус режимов ior-core</p>{nativeState?.core?.options.find(option=>option.id==="iorCore")?.combinations?.map(item=><div key={`${item.protocol}-${item.mode}`}><span>{item.protocol} · {item.mode.toUpperCase()}</span><small>{item.status==="supported"?"Доступно · проверено":item.status==="experimental"?"Экспериментально · тест при подключении":item.status==="unverifiedWindowsVersion"?"TUN требует Windows 11":"Включите экспериментальный режим"} · {item.implementation}</small></div>)}</div><Setting title="Разрешить UDP/443 через Reality — экспериментально" text="Только ior-core. Проверка QUIC/HTTP3; применяется при новом подключении. Исходный профиль сохраняется."><Toggle value={Boolean(nativeSettings.realityUdp443Enabled)} onChange={value=>updateNative({realityUdp443Enabled:value})}/></Setting><Setting title="Экономить ресурсы" text="Реже проверять сервисы и задержку; обновлять показатели раз в 2–3 секунды"><Toggle value={Boolean(nativeSettings.powerSavingMode)} onChange={value => updateNative({ powerSavingMode: value })}/></Setting></section>}
    {settingsTab === "connection" && <section className="content-card settings-section"><Setting title="Автопроверка конфигурации" text="Проверять sing-box при обновлении подписок"><Toggle value={nativeSettings.autoValidateConfig !== false} onChange={value => updateNative({ autoValidateConfig: value })}/></Setting></section>}
    {settingsTab === "general" && <section className="content-card settings-section"><div className="card-title"><div><span>Приложение</span><h3>Общие настройки</h3></div></div><Setting title="Путь к sing-box" text="Исполняемый файл ядра"><input className="setting-input wide" value="core/sing-box.exe" readOnly/></Setting><Setting title="Сворачивать в трей" text="Оставлять соединение активным"><Toggle value={Boolean(nativeSettings.minimizeToTray)} onChange={value => updateNative({ minimizeToTray: value })}/></Setting><Setting title="Язык" text="Язык интерфейса"><AnimatedSelect className="language-select" value={String(nativeSettings.language || "russian").toLowerCase().startsWith("en") ? "english" : "russian"} onChange={value => updateNative({ language: value })} options={[{ value: "russian", label: "Русский" }, { value: "english", label: "English" }]}/></Setting><div className="layout-actions"><button onClick={() => void exportBackup()}>Экспорт всей конфигурации</button><button onClick={importBackup}>Восстановить из копии</button></div></section>}
    {settingsTab === "connection" && <section className="content-card settings-section"><div className="card-title"><div><span>Соединение</span><h3>TUN и локальный прокси</h3></div></div><Setting title="Режим TUN" text="Перенаправлять системный трафик"><Toggle value={Boolean(nativeSettings.tunEnabled)} onChange={value => updateNative({ tunEnabled: value })}/></Setting><Setting title="Локальный HTTP/SOCKS" text="Mixed proxy на 127.0.0.1"><Toggle value={Boolean(nativeSettings.mixedProxyEnabled)} onChange={value => updateNative({ mixedProxyEnabled: value })}/></Setting><Setting title="Порт прокси" text="Локальный mixed inbound"><input className="setting-input number" type="number" key={String(nativeSettings.mixedProxyPort)} defaultValue={Number(nativeSettings.mixedProxyPort || 2080)} onBlur={event => updateNative({ mixedProxyPort: Number(event.target.value) })}/></Setting><Setting title="Адрес TUN" text="Виртуальная подсеть интерфейса"><input className="setting-input" key={String(nativeSettings.tunAddress)} defaultValue={String(nativeSettings.tunAddress || "172.19.0.1/30")} onBlur={event => updateNative({ tunAddress: event.target.value })}/></Setting><Setting title="MTU" text="1280–10000, для Windows 10 до 1500"><input className="setting-input number" type="number" key={String(nativeSettings.tunMtu)} defaultValue={Number(nativeSettings.tunMtu || 9000)} onBlur={event => updateNative({ tunMtu: Number(event.target.value) })}/></Setting><div className="refresh-settings"><div className="refresh-settings-head"><span><small>Подписки</small><strong>Автоматическое обновление</strong></span><div className="refresh-master"><em>{Intl.DateTimeFormat().resolvedOptions().timeZone}</em><motion.button className={`toggle ${autoRefreshEnabled ? "active" : ""}`} onClick={() => { const enabled = !autoRefreshEnabled; setAutoRefreshEnabled(enabled); updateNative({ autoRefreshSubscriptions: enabled }); }} whileTap={{ scale: .93 }} aria-pressed={autoRefreshEnabled}><motion.span animate={{ x: autoRefreshEnabled ? 19 : 0 }}/></motion.button></div></div><p>{autoRefreshEnabled ? "Каждая подписка работает по своему таймеру и обновляется независимо от остальных." : "Все автоматические обновления приостановлены. Интервалы подписок сохранены."}</p>{(nativeState?.subscriptions || []).map(item => { const id = String(item.id); return <SubscriptionRefreshRow key={id} id={id} name={String(item.name)} globalEnabled={autoRefreshEnabled} enabled={refreshEnabled[id] !== false} intervalHours={refreshIntervals[id] || 12} nextRefreshUtc={item.nextRefreshUtc ? String(item.nextRefreshUtc) : undefined} onEnabled={enabled => { setRefreshEnabled(values => ({ ...values, [id]: enabled })); updateNative({ subscriptionRefresh: { [id]: { enabled } } }); }} onInterval={intervalHours => { setRefreshIntervals(values => ({ ...values, [id]: intervalHours })); updateNative({ subscriptionRefresh: { [id]: { intervalHours } } }); }}/>; })}</div></section>}
    {settingsTab === "dns" && <section className="content-card settings-section"><div className="card-title"><div><span>Разрешение имён</span><h3>Настройки DNS</h3></div></div><Setting title="DNS-сервер" text="IP-адрес или DNS-over-HTTPS"><input className="setting-input wide" key={String(nativeSettings.dnsServer)} defaultValue={String(nativeSettings.dnsServer || "1.1.1.1")} onBlur={event => updateNative({ dnsServer: event.target.value })}/></Setting><Setting title="Bootstrap DNS" text="Прямой IPv4 DNS до запуска TUN"><input className="setting-input" key={String(nativeSettings.bootstrapDnsServer)} defaultValue={String(nativeSettings.bootstrapDnsServer || "1.1.1.1")} onBlur={event => updateNative({ bootstrapDnsServer: event.target.value })}/></Setting><Setting title="Стратегия" text="Предпочтительное семейство адресов"><AnimatedSelect className="dns-strategy-select" value={String(nativeSettings.dnsStrategy || "prefer_ipv4")} onChange={value => updateNative({ dnsStrategy: value })} options={[{ value: "prefer_ipv4", label: "Предпочитать IPv4" }, { value: "prefer_ipv6", label: "Предпочитать IPv6" }, { value: "ipv4_only", label: "Только IPv4" }, { value: "ipv6_only", label: "Только IPv6" }]}/></Setting></section>}
    {settingsTab === "advanced" && <section className="content-card settings-section"><div className="card-title"><div><span>sing-box</span><h3>Расширенные параметры</h3></div></div><Setting title="Анализ трафика" text="Определять протокол назначения"><Toggle value={Boolean(nativeSettings.sniffEnabled)} onChange={value => updateNative({ sniffEnabled: value })}/></Setting><Setting title="Автоматический маршрут" text="Добавлять системные маршруты"><Toggle value={Boolean(nativeSettings.autoRoute)} onChange={value => updateNative({ autoRoute: value })}/></Setting><Setting title="Строгая маршрутизация" text="Не допускать обход TUN"><Toggle value={Boolean(nativeSettings.strictRoute)} onChange={value => updateNative({ strictRoute: value })}/></Setting><Setting title="Системный стек" text="Использовать stack: system"><Toggle value={Boolean(nativeSettings.stackSystem)} onChange={value => updateNative({ stackSystem: value })}/></Setting><Setting title="Системный прокси Windows" text="Применять и восстанавливать автоматически"><Toggle value={Boolean(nativeSettings.setSystemProxy)} onChange={value => updateNative({ setSystemProxy: value })}/></Setting><Setting title="Разрешить небезопасные сертификаты" text="Только для доверенных профилей"><Toggle value={Boolean(nativeSettings.allowInsecureCertificates)} onChange={value => updateNative({ allowInsecureCertificates: value })}/></Setting><Setting title="Уровень журнала" text="Подробность сообщений ядра"><AnimatedSelect className="log-detail-select" value={String(nativeSettings.logLevel || "info")} onChange={value => updateNative({ logLevel: value })} options={["debug", "info", "warn", "error"].map(item => ({ value: item, label: item }))}/></Setting></section>}
    {settingsTab === "appearance" && <div className="appearance-stack"><section className="content-card settings-section"><div className="card-title"><div><span>Навигация</span><h3>Поведение интерфейса</h3></div></div><Setting title="Положение панели" text="Слева или сверху"><AnimatedSelect className="sidebar-position-select" value={navigationPosition} onChange={value => { onNavigationPosition(value as "left" | "top"); updateNative({ navPanelPosition: value }); }} options={[{ value: "left", label: "Слева" }, { value: "top", label: "Сверху" }]}/></Setting><Setting title="Подсказки навигации" text="Показывать названия при наведении"><Toggle value={Boolean(nativeSettings.showNavTooltips)} onChange={value => updateNative({ showNavTooltips: value })}/></Setting><Setting title="Разделители" text="Визуально группировать пункты"><Toggle value={Boolean(nativeSettings.showNavSeparators)} onChange={value => updateNative({ showNavSeparators: value })}/></Setting><Setting title="Эффект стекла" text="Прозрачность и размытие панелей"><Toggle value={Boolean(nativeSettings.usePanelGlass)} onChange={value => updateNative({ usePanelGlass: value })}/></Setting></section><section className="content-card theme-settings"><div className="card-title"><div><span>Оформление</span><h3>Тема интерфейса</h3></div><small>7 тёмных · 7 светлых</small></div><div className="theme-groups"><div><h4>Тёмные</h4><div className="theme-grid">{themes.filter(theme => theme.mode === "dark").map(theme => <ThemeButton key={theme.id} theme={theme} active={themeId === theme.id} onClick={() => selectTheme(theme.id)}/>)}</div></div><div><h4>Светлые</h4><div className="theme-grid">{themes.filter(theme => theme.mode === "light").map(theme => <ThemeButton key={theme.id} theme={theme} active={themeId === theme.id} onClick={() => selectTheme(theme.id)}/>)}</div></div></div></section></div>}
  </motion.div></AnimatePresence></div>;
}

function ThemeButton({ theme, active, onClick }: { theme: (typeof themes)[number]; active: boolean; onClick: () => void }) { return <motion.button className={`theme-option ${active ? "active" : ""}`} onClick={onClick} whileHover={{ y: -2 }} whileTap={{ scale: .98 }} aria-pressed={active}><span className="theme-preview" style={{ background: `linear-gradient(135deg, ${theme.bg} 0 45%, ${theme.panel} 45% 72%, ${theme.accent} 72%)` }}><i style={{ background: theme.text }}/><b style={{ background: theme.accent2 }}/></span><span><strong>{theme.name}</strong><small>{theme.mode === "dark" ? "Тёмная" : "Светлая"}</small></span>{active && <motion.em layoutId="theme-selected"><Icon size={12}><path d="m4 12 5 5L20 6"/></Icon></motion.em>}</motion.button>; }

type AnimatedSelectOption = { value: string; label: string };
function AnimatedSelect({ value, defaultValue, options, onChange, disabled = false, className = "" }: { value?: string | number; defaultValue?: string | number; options: AnimatedSelectOption[]; onChange?: (value: string) => void; disabled?: boolean; className?: string }) {
  const [internalValue, setInternalValue] = useState(String(defaultValue ?? options[0]?.value ?? ""));
  const [open, setOpen] = useState(false);
  const [openUp, setOpenUp] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const current = String(value ?? internalValue);
  const selected = options.find(option => option.value === current) ?? options[0];
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") setOpen(false); };
    window.addEventListener("pointerdown", outside); window.addEventListener("keydown", escape);
    return () => { window.removeEventListener("pointerdown", outside); window.removeEventListener("keydown", escape); };
  }, [open]);
  const choose = (next: string) => { setInternalValue(next); onChange?.(next); setOpen(false); };
  const toggleOpen = () => { if (!open && root.current) { const rect = root.current.getBoundingClientRect(); setOpenUp(window.innerHeight - rect.bottom < Math.min(270, options.length * 39 + 18) && rect.top > 280); } setOpen(item => !item); };
  return <div ref={root} className={`animated-select ${open ? "open" : ""} ${openUp ? "open-up" : ""} ${disabled ? "disabled" : ""} ${className}`}><motion.button type="button" className="animated-select-trigger" disabled={disabled} aria-haspopup="listbox" aria-expanded={open} onClick={toggleOpen} whileTap={disabled ? undefined : { scale: .98 }}><span>{selected?.label}</span><motion.i animate={{ rotate: open ? 180 : 0 }} transition={{ type: "spring", stiffness: 430, damping: 30 }}><Icon size={14}><path d="m7 9 5 5 5-5"/></Icon></motion.i></motion.button><AnimatePresence>{open && <motion.div className="animated-select-menu" role="listbox" initial={{ opacity: 0, y: openUp ? 9 : -9, scale: .96, filter: "blur(6px)" }} animate={{ opacity: 1, y: 0, scale: 1, filter: "blur(0px)" }} exit={{ opacity: 0, y: openUp ? 6 : -6, scale: .97, filter: "blur(4px)" }} transition={{ type: "spring", stiffness: 440, damping: 32, mass: .76 }}><motion.div initial="closed" animate="open" variants={{ closed: {}, open: { transition: { staggerChildren: .025, delayChildren: .025 } } }}>{options.map(option => <motion.button type="button" role="option" aria-selected={option.value === current} className={option.value === current ? "selected" : ""} key={option.value} onClick={() => choose(option.value)} variants={{ closed: { opacity: 0, x: -7 }, open: { opacity: 1, x: 0 } }} transition={{ duration: .16 }}><span>{option.label}</span>{option.value === current && <motion.i layoutId={`select-check-${className || "default"}`}><Icon size={13}><path d="m4 12 5 5L20 6"/></Icon></motion.i>}</motion.button>)}</motion.div></motion.div>}</AnimatePresence></div>;
}

function Toggle({ initial = false, storageKey, value, onChange }: { initial?: boolean; storageKey?: string; value?: boolean; onChange?: (value: boolean) => void }) {
  const [active, setActive] = useState(() => storageKey ? window.localStorage.getItem(storageKey) === null ? initial : window.localStorage.getItem(storageKey) === "true" : initial);
  useEffect(() => { if (storageKey) window.localStorage.setItem(storageKey, String(active)); }, [active, storageKey]);
  const current = value ?? active;
  return <motion.button className={`toggle ${current ? "active" : ""}`} style={{ flex: "0 0 43px", width: 43, minWidth: 43, position: "relative" }} onClick={() => { const next = !current; if (value === undefined) setActive(next); onChange?.(next); }} whileTap={{ scale: .93 }} aria-pressed={current}><motion.span style={{ position: "absolute", left: 3, top: 3 }} animate={{ x: current ? 19 : 0 }}/></motion.button>;
}
function Setting({ title, text, children }: { title: string; text: string; children: ReactNode }) { return <div className="setting"><div><strong>{title}</strong><small>{text}</small></div>{children}</div>; }

function ImportModal({ onClose, onDone }: { onClose: () => void; onDone: (profile: { name: string; protocol: string; kind: "subscription" | "config"; text: string }) => void | Promise<void> }) {
  const tabs = [{ id: "link", label: "Ссылка", placeholder: "Вставьте vless://, hysteria2://, ss:// или другую ссылку…", hint: "Одна ссылка на готовый профиль" }, { id: "subscription", label: "Подписка", placeholder: "https://example.com/api/subscription…", hint: "Список серверов будет обновляться автоматически" }, { id: "wireguard", label: "WireGuard", placeholder: "Вставьте содержимое конфигурации WireGuard…", hint: "Поддерживается стандартный формат .conf" }] as const;
  const [tab, setTab] = useState<(typeof tabs)[number]["id"]>("link");
  const [values, setValues] = useState<Record<string, string>>({ link: "", subscription: "", wireguard: "" });
  const [name, setName] = useState("");
  const [submitError, setSubmitError] = useState("");
  const active = tabs.find(item => item.id === tab)!;
  const value = values[tab];
  const detected = tab === "subscription" ? (value.startsWith("http") ? "Ссылка подписки распознана" : "Ожидается URL подписки") : tab === "wireguard" ? (value.includes("[Interface]") && value.includes("[Peer]") ? "WireGuard-конфигурация распознана" : "Нужны секции [Interface] и [Peer]") : value.match(/^(vless|vmess|trojan|ss|hysteria2|hy2|wireguard|vpn|socks|socks5|http|https):\/\//i)?.[1]?.toUpperCase() ? `${value.match(/^([a-z0-9]+):\/\//i)?.[1].toUpperCase()} распознан` : "Поддерживаются VLESS, VMess, Trojan, SS, Hysteria2, WireGuard, SOCKS и HTTP";
  const paste = async () => { try { const text = await navigator.clipboard.readText(); setValues(items => ({ ...items, [tab]: text })); } catch { /* clipboard permission depends on the browser */ } };
  const submit = async () => { setSubmitError(""); const scheme = value.match(/^([a-z0-9]+):\/\//i)?.[1]?.toLowerCase(); const protocol = tab === "wireguard" ? "WireGuard" : tab === "subscription" ? "Подписка" : ({ vless: "VLESS · Reality", vmess: "VMess", trojan: "Trojan · TLS", ss: "Shadowsocks", hysteria2: "Hysteria2", hy2: "Hysteria2", wireguard: "WireGuard", socks: "SOCKS5", socks5: "SOCKS5", http: "HTTP", https: "HTTPS" } as Record<string, string>)[scheme || ""] || "VLESS"; const fallback = tab === "subscription" ? "Новая подписка" : protocol; try { await onDone({ name: name.trim() || fallback, protocol, kind: tab === "subscription" ? "subscription" : "config", text: value.trim() }); } catch (error) { setSubmitError(error instanceof Error ? error.message : "Не удалось импортировать профиль"); } };
  return <motion.div className="modal-layer" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onMouseDown={onClose}><motion.div className="modal import-modal" initial={{ opacity: 0, scale: .94, y: 18 }} animate={{ opacity: 1, scale: 1, y: 0 }} exit={{ opacity: 0, scale: .96, y: 8 }} transition={{ type: "spring", duration: .28, bounce: .06 }} onMouseDown={event => event.stopPropagation()}><div className="modal-head"><div><span>Новый профиль</span><h2>Добавить сервер</h2></div><button onClick={onClose}>×</button></div><label className="import-name"><span>Название (необязательно)</span><input value={name} onChange={event => setName(event.target.value)} placeholder="Будет взято из ссылки или подписки"/></label><div className="modal-tabs" role="tablist" aria-label="Способ импорта">{tabs.map(item => <button key={item.id} role="tab" aria-selected={tab === item.id} className={tab === item.id ? "active" : ""} onClick={() => setTab(item.id)}>{tab === item.id && <motion.i layoutId="import-tab-slider" transition={{ type: "spring", duration: .25, bounce: .05 }}/>}<span>{item.label}</span></button>)}</div><AnimatePresence mode="wait"><motion.div key={tab} className="import-pane" initial={{ opacity: 0, x: 12 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -10 }} transition={{ duration: .16 }}><div className="import-textarea-wrap"><textarea value={value} onChange={event => setValues(items => ({ ...items, [tab]: event.target.value }))} autoFocus placeholder={active.placeholder}/><button onClick={paste}>Вставить</button></div><p className={value ? "detected" : ""}><Icon size={14}><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/></Icon>{value ? detected : active.hint}</p></motion.div></AnimatePresence><div className="modal-actions"><button className="secondary" onClick={onClose}>Отмена</button><button className="primary" disabled={!value.trim()} onClick={submit}>Добавить профиль</button></div></motion.div></motion.div>;
}

function time(seconds: number) { const m = String(Math.floor(seconds / 60)).padStart(2, "0"); const s = String(seconds % 60).padStart(2, "0"); return `${m}:${s}`; }

createRoot(document.getElementById("root")!).render(<App/>);
