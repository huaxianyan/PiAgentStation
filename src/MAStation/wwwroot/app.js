'use strict';

/* ------------------------------------------------------------------ *
 * 与中心通信
 *
 * 页面由中心托管，命令与事件都走同一条 WebSocket，所以桌面壳和手机浏览器
 * 用的是同一套前端代码。令牌由中心生成，桌面壳拼在地址上，手机手动带上。
 * ------------------------------------------------------------------ */

const transport = {
  socket: null,
  connected: false,
  attempts: 0,
  queue: [],
  token: new URLSearchParams(location.search).get('token') || '',
};

function send(message) {
  if (transport.connected && transport.socket && transport.socket.readyState === WebSocket.OPEN) {
    transport.socket.send(JSON.stringify(message));
    return true;
  }

  // 掉线时只留住能自愈的请求，别的操作直接告诉用户没连上。
  if (message.type === 'list_conversations' && !transport.queue.some((item) => item.type === 'list_conversations')) {
    transport.queue.push(message);
  }
  return false;
}

function setConnectionState(text, ok) {
  el.connStatus.textContent = app.centerName ? text + ' · ' + app.centerName : text;
  el.connStatus.className = 'conn' + (ok === true ? ' ok' : ok === false ? ' off' : '');
}

function connect() {
  if (!transport.token) {
    setConnectionState('这个地址缺少访问令牌，从中心启动时打印的地址打开', false);
    return;
  }

  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  const socket = new WebSocket(`${scheme}://${location.host}/ws?token=${encodeURIComponent(transport.token)}`);
  transport.socket = socket;
  setConnectionState(transport.attempts ? '正在重连…' : '正在连接…');

  socket.addEventListener('open', () => {
    transport.connected = true;
    transport.attempts = 0;
    setConnectionState('已连接', true);
    while (transport.queue.length) {
      socket.send(JSON.stringify(transport.queue.shift()));
    }
    send({ type: 'list_conversations' });
  });

  socket.addEventListener('message', (event) => {
    let data;
    try {
      data = JSON.parse(event.data);
    } catch {
      return;
    }
    if (data && typeof data === 'object') {
      dispatch(data);
    }
  });

  socket.addEventListener('close', () => {
    transport.connected = false;
    transport.attempts += 1;
    setConnectionState('连接断开，正在自动重连', false);
    const delay = Math.min(1000 * 2 ** (transport.attempts - 1), 15000);
    setTimeout(connect, delay);
  });

  socket.addEventListener('error', () => {
    // 具体原因看 close。
  });
}

/* ------------------------------------------------------------------ *
 * 状态
 * ------------------------------------------------------------------ */

const app = {
  sessions: [],
  sessionRoot: '',
  storePath: '',
  centerVersion: '',
  pendingPrompt: '',
  centerName: '',
  filter: '',
  currentKey: null,
  byKey: new Map(),
  activeCwd: '',
  lastCwd: '',
  pendingCwd: '',
  openRequest: null,
  attachments: [],
  extStatus: new Map(),
  widgets: new Map(),
  dirty: new Set(),
  frame: 0,
  nodeCache: new Map(),
};

function current() {
  return app.currentKey ? app.byKey.get(app.currentKey) : null;
}

function makeSession(key, state, messages, cwd) {
  const built = buildItems(messages || []);
  return {
    key,
    state: state || {},
    cwd: cwd || '',
    items: built.items,
    toolIndex: built.toolIndex,
    streaming: false,
    stats: null,
    models: null,
    levels: null,
    liveId: null,
    seq: built.items.length,
  };
}

const el = {
  sessionList: document.getElementById('sessionList'),
  sessionFilter: document.getElementById('sessionFilter'),
  filterToggle: document.getElementById('filterToggle'),
  filterbar: document.getElementById('filterbar'),
  navCount: document.getElementById('navCount'),
  settingsBtn: document.getElementById('settingsBtn'),
  collapseBtn: document.getElementById('collapseBtn'),
  sidebarToggle: document.getElementById('sidebarToggle'),
  exportBtn: document.getElementById('exportBtn'),
  filesBtn: document.getElementById('filesBtn'),
  filePanel: document.getElementById('filePanel'),
  filesPath: document.getElementById('filesPath'),
  filesBody: document.getElementById('filesBody'),
  filesUp: document.getElementById('filesUp'),
  filesClose: document.getElementById('filesClose'),
  terminalBtn: document.getElementById('terminalBtn'),
  jumpBottom: document.getElementById('jumpBottom'),
  content: document.getElementById('content'),
  terminalPanel: document.getElementById('terminalPanel'),
  terminalShell: document.getElementById('terminalShell'),
  terminalBody: document.getElementById('terminalBody'),
  terminalInput: document.getElementById('terminalInput'),
  terminalClear: document.getElementById('terminalClear'),
  terminalStop: document.getElementById('terminalStop'),
  terminalClose: document.getElementById('terminalClose'),
  workspacePill: document.getElementById('workspacePill'),
  workspacePillText: document.getElementById('workspacePillText'),
  wsSelectText: document.getElementById('wsSelectText'),
  content: document.getElementById('content'),
  windowEl: document.getElementById('app'),
  crumbSep: document.getElementById('crumbSep'),
  sessionTitle: document.getElementById('sessionTitle'),
  renameBtn: document.getElementById('renameBtn'),
  sessionCwd: document.getElementById('sessionCwd'),
  thread: document.getElementById('thread'),
  threadInner: document.getElementById('threadInner'),
  composer: document.getElementById('composer'),
  empty: document.getElementById('empty'),
  input: document.getElementById('input'),
  sendBtn: document.getElementById('sendBtn'),
  attachBtn: document.getElementById('attachBtn'),
  attachments: document.getElementById('attachments'),
  hint: document.getElementById('hint'),
  modelSelect: document.getElementById('modelSelect'),
  thinkingSelect: document.getElementById('thinkingSelect'),
  compactBtn: document.getElementById('compactBtn'),
  ctxUsage: document.getElementById('ctxUsage'),
  newSessionBtn: document.getElementById('newSessionBtn'),
  emptyNewBtn: document.getElementById('emptyNewBtn'),
  overlay: document.getElementById('overlay'),
  overlayText: document.getElementById('overlayText'),
  modal: document.getElementById('modal'),
  toasts: document.getElementById('toasts'),
  extStatus: document.getElementById('extStatus'),
  connStatus: document.getElementById('connStatus'),
  widgetStrip: document.getElementById('widgetStrip'),
  completions: document.getElementById('completions'),
  fileInput: document.getElementById('fileInput'),
};

const DEFAULT_CSS = `body,
input,
textarea,
keygen,
select,
button {
  text-shadow: 0px 0px 1px rgba(0, 0, 0, 0.16) !important;
  -webkit-font-smoothing: subpixel-antialiased !important;
  font-family: 'Microsoft Yahei';
}

@font-face {
  font-family: 'Microsoft YaHei';
  src: local('Microsoft Yahei');
}

@font-face {
  font-family: 'Microsoft YaHei';
  src: local('Segoe UI');
}

@font-face {
  font-family: '微软雅黑';
  src: local('Microsoft Yahei');
}

@font-face {
  font-family: '微软雅黑';
  src: local('Segoe UI');
}

@font-face {
  font-family: 'Segoe UI';
  src: local('Microsoft Yahei');
}

@font-face {
  font-family: 'Segoe UI';
  src: local('Segoe UI');
}
`;

/* ------------------------------------------------------------------ *
 * 通用小工具
 * ------------------------------------------------------------------ */

const ANSI = /\x1b\[[0-9;]*m/g;
const stripAnsi = (text) => (text || '').replace(ANSI, '');

function escapeHtml(text) {
  return String(text == null ? '' : text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function escapeAttr(text) {
  return escapeHtml(text).replace(/'/g, '&#39;');
}

function formatBytes(n) {
  if (!n) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(0) + ' KB';
  return (n / 1024 / 1024).toFixed(1) + ' MB';
}

function relativeTime(ms) {
  const diff = Date.now() - ms;
  if (diff < 60_000) return '刚刚';
  if (diff < 3_600_000) return Math.floor(diff / 60_000) + ' 分钟前';
  if (diff < 86_400_000) return Math.floor(diff / 3_600_000) + ' 小时前';
  if (diff < 7 * 86_400_000) return Math.floor(diff / 86_400_000) + ' 天前';
  const d = new Date(ms);
  return `${d.getMonth() + 1}-${String(d.getDate()).padStart(2, '0')}`;
}

function baseName(path) {
  const parts = String(path || '').split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}

function toast(message, kind) {
  const node = document.createElement('div');
  node.className = 'toast' + (kind ? ' ' + kind : '');
  node.textContent = message;
  el.toasts.appendChild(node);
  setTimeout(() => node.remove(), kind === 'error' ? 9000 : 4500);
}

function setOverlay(text) {
  if (!text) {
    el.overlay.hidden = true;
    return;
  }
  el.overlayText.textContent = text;
  el.overlay.hidden = false;
}

/* ------------------------------------------------------------------ *
 * 极简 Markdown（够用于聊天正文与工具输出）
 * ------------------------------------------------------------------ */

function renderInline(text) {
  const source = String(text == null ? '' : text);
  const pattern = /(`[^`\n]+`)|(\*\*[^*\n]+\*\*)|(\*[^*\n]+\*)|(\[[^\]\n]+\]\([^)\s]+\))|(https?:\/\/[^\s<>()]+)/g;
  let out = '';
  let last = 0;
  for (const match of source.matchAll(pattern)) {
    out += escapeHtml(source.slice(last, match.index));
    const token = match[0];
    if (token.startsWith('`')) {
      out += '<code>' + escapeHtml(token.slice(1, -1)) + '</code>';
    } else if (token.startsWith('**')) {
      out += '<strong>' + escapeHtml(token.slice(2, -2)) + '</strong>';
    } else if (token.startsWith('*')) {
      out += '<em>' + escapeHtml(token.slice(1, -1)) + '</em>';
    } else if (token.startsWith('[')) {
      const link = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(token);
      out += '<a href="' + escapeAttr(link[2]) + '" target="_blank" rel="noreferrer">' +
        escapeHtml(link[1]) + '</a>';
    } else {
      out += '<a href="' + escapeAttr(token) + '" target="_blank" rel="noreferrer">' +
        escapeHtml(token) + '</a>';
    }
    last = match.index + token.length;
  }
  out += escapeHtml(source.slice(last));
  return out;
}

function renderMarkdown(text) {
  const source = String(text == null ? '' : text).replace(/\r\n/g, '\n');
  const chunks = [];
  const fence = /```([\w+-]*)\n([\s\S]*?)(?:```|$)/g;
  let last = 0;
  for (const match of source.matchAll(fence)) {
    if (match.index > last) {
      chunks.push({ code: false, body: source.slice(last, match.index) });
    }
    const lang = match[1] ? ' data-lang="' + escapeAttr(match[1]) + '"' : '';
    chunks.push({ code: true, body: match[2], lang });
    last = match.index + match[0].length;
  }
  if (last < source.length) {
    chunks.push({ code: false, body: source.slice(last) });
  }

  let html = '';
  for (const chunk of chunks) {
    if (chunk.code) {
      const code = chunk.body.replace(/\n$/, '');
      html += '<div class="code-block"><button class="code-copy" data-code="' +
        escapeAttr(encodeURIComponent(code)) + '">复制</button><pre><code>' + escapeHtml(code) +
        '</code></pre></div>';
    } else {
      html += renderBlocks(chunk.body);
    }
  }
  return html;
}

function renderBlocks(body) {
  const lines = body.split('\n');
  const out = [];
  let paragraph = [];
  let list = null;

  const flushParagraph = () => {
    if (paragraph.length) {
      out.push('<p>' + renderInline(paragraph.join('\n')).replace(/\n/g, '<br />') + '</p>');
      paragraph = [];
    }
  };
  const flushList = () => {
    if (list && list.items.length) {
      out.push(renderListTree(list.items, 0).html);
      list = null;
    }
  };
  const pushListEntry = (tag, depth, text) => {
    if (!list) list = { items: [] };
    list.items.push({ tag, depth, text });
  };

  const tableCells = (line) => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => cell.trim());
  const isTableRow = (line) => /^\|.*\|$/.test(line.trim());
  const isSeparator = (line) => /^\|?[\s:|-]*-{2,}[\s:|-]*\|?$/.test(line.trim()) && line.includes('-');

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    if (!trimmed) {
      flushParagraph();
      flushList();
      continue;
    }

    const heading = /^(#{1,4})\s+(.*)$/.exec(trimmed);
    if (heading) {
      flushParagraph();
      flushList();
      const level = Math.min(heading[1].length + 1, 4);
      out.push('<h' + level + '>' + renderInline(heading[2]) + '</h' + level + '>');
      continue;
    }

    if (/^([-*_])\1{2,}$/.test(trimmed)) {
      flushParagraph();
      flushList();
      out.push('<hr />');
      continue;
    }

    const quote = /^>\s?(.*)$/.exec(trimmed);
    if (quote) {
      flushParagraph();
      flushList();
      out.push('<blockquote>' + renderInline(quote[1]) + '</blockquote>');
      continue;
    }

    // 表格：表头 + 分隔行 + 若干行，整体渲染成一张表。
    if (isTableRow(trimmed) && i + 1 < lines.length && isSeparator(lines[i + 1])) {
      flushParagraph();
      flushList();

      const header = tableCells(trimmed);
      const aligns = tableCells(lines[i + 1]).map((cell) => {
        const left = cell.startsWith(':');
        const right = cell.endsWith(':');
        if (left && right) return 'center';
        if (right) return 'right';
        if (left) return 'left';
        return '';
      });

      const alignAttr = (index) => (aligns[index] ? ' class="align-' + aligns[index] + '"' : '');
      let html = '<div class="table-wrap"><table><thead><tr>' +
        header.map((cell, index) => '<th' + alignAttr(index) + '>' + renderInline(cell) + '</th>').join('') +
        '</tr></thead><tbody>';

      i += 2;
      for (; i < lines.length && isTableRow(lines[i]); i++) {
        const cells = tableCells(lines[i]);
        html += '<tr>' + header.map((_, index) =>
          '<td' + alignAttr(index) + '>' + renderInline(cells[index] || '') + '</td>').join('') + '</tr>';
      }
      i--;
      out.push(html + '</tbody></table></div>');
      continue;
    }

    const bullet = /^([-*+])\s+(.*)$/.exec(trimmed);
    if (bullet) {
      flushParagraph();
      const depth = Math.floor((line.length - line.trimStart().length) / 2);
      pushListEntry('ul', depth, bullet[2]);
      continue;
    }

    const ordered = /^\d+[.)]\s+(.*)$/.exec(trimmed);
    if (ordered) {
      flushParagraph();
      const depth = Math.floor((line.length - line.trimStart().length) / 2);
      pushListEntry('ol', depth, ordered[1]);
      continue;
    }

    if (list) {
      // 列表项的续行
      list.items[list.items.length - 1].text += '\n' + trimmed;
      continue;
    }

    paragraph.push(trimmed);
  }

  flushParagraph();
  flushList();
  return out.join('');
}

/** 按缩进还原嵌套列表：两层以上也照原样缩进去。 */
function renderListTree(entries, index) {
  const tag = entries[index].tag;
  let html = '<' + tag + '>';
  const depth = entries[index].depth;

  while (index < entries.length && entries[index].depth >= depth) {
    if (entries[index].depth > depth) {
      const nested = renderListTree(entries, index);
      html += nested.html;
      index = nested.index;
      continue;
    }

    let inner = renderInline(entries[index].text);
    index += 1;

    if (index < entries.length && entries[index].depth > depth) {
      const nested = renderListTree(entries, index);
      inner += nested.html;
      index = nested.index;
    }

    html += '<li>' + inner + '</li>';
  }

  return { html: html + '</' + tag + '>', index };
}

function blocksFromMessage(message) {
  const content = message && message.content;
  if (typeof content === 'string') {
    return [{ type: 'text', text: content }];
  }
  if (!Array.isArray(content)) return [];
  return content.map((block) => {
    if (block.type === 'text') return { type: 'text', text: block.text || '' };
    if (block.type === 'thinking') return { type: 'thinking', text: block.thinking || '' };
    if (block.type === 'toolCall') {
      return {
        type: 'toolCall',
        id: block.id,
        name: block.name,
        args: block.arguments || {},
        argsText: JSON.stringify(block.arguments || {}, null, 2),
        output: '',
        running: false,
        done: false,
        isError: false,
        durationMs: null,
      };
    }
    if (block.type === 'image') {
      return { type: 'image', url: 'data:' + block.mimeType + ';base64,' + block.data };
    }
    return null;
  }).filter(Boolean);
}

function buildItems(messages) {
  const items = [];
  const toolIndex = new Map();
  let seq = 0;

  for (const message of messages) {
    if (!message) continue;
    if (message.role === 'user') {
      items.push({
        id: 'i' + seq++,
        kind: 'user',
        text: contentText(message.content),
        images: contentImages(message.content),
      });
    } else if (message.role === 'assistant') {
      const item = { id: 'i' + seq++, kind: 'assistant', blocks: blocksFromMessage(message), streaming: false };
      items.push(item);
      for (const block of item.blocks) {
        if (block.type === 'toolCall' && block.id) toolIndex.set(block.id, { item, block });
      }
    } else if (message.role === 'toolResult') {
      const hit = toolIndex.get(message.toolCallId);
      if (hit) {
        hit.block.output = contentText(message.content);
        hit.block.isError = Boolean(message.isError);
        hit.block.done = true;
      }
    }
  }

  return { items, toolIndex };
}

/* ------------------------------------------------------------------ *
 * 渲染：会话列表
 * ------------------------------------------------------------------ */

function dateGroup(ms) {
  const today = new Date();
  const startOfToday = new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime();
  if (ms >= startOfToday) return '今天';
  if (ms >= startOfToday - 86_400_000) return '昨天';
  if (ms >= startOfToday - 7 * 86_400_000) return '本周';
  return '更早';
}

function renderSessionList() {
  const typed = app.filter.trim();
  const filter = typed.toLowerCase();
  // 中心带回搜索结果时按内容命中显示，否则只在本地按标题过滤。
  const searching = Boolean(typed) && app.searchResults?.query === typed;
  const pool = searching ? app.searchResults.items : app.sessions;
  const groups = new Map();

  for (const conversation of pool) {
    if (filter && !searching) {
      const haystack = [(conversation.title || ''), conversation.cwd].join(' ').toLowerCase();
      if (!haystack.includes(filter)) continue;
    }
    const label = searching ? '搜索结果' : dateGroup(conversation.updatedAt);
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label).push(conversation);
  }

  el.navCount.textContent = app.sessions.length ? String(app.sessions.length) : '';


  el.sessionList.innerHTML = '';

  if (searching && groups.size) {
    const hint = document.createElement('div');
    hint.className = 'group-label';
    hint.textContent = '按内容命中 ' + pool.length + ' 条';
    el.sessionList.appendChild(hint);
  }

  if (!groups.size) {
    const empty = document.createElement('div');
    empty.className = 'group-label';
    empty.textContent = typed ? '没有匹配的对话' : '还没有对话';
    el.sessionList.appendChild(empty);
    return;
  }

  for (const [label, conversations] of groups) {
    if (!searching) {
      const groupLabel = document.createElement('div');
      groupLabel.className = 'group-label';
      groupLabel.textContent = label;
      el.sessionList.appendChild(groupLabel);
    }

    for (const conversation of conversations) {
      const item = document.createElement('div');
      item.className = 'session-item' + (conversation.id === app.currentKey ? ' active' : '');
      item.title = conversation.cwd;

      const open = document.createElement('button');
      open.className = 'session-open';
      open.innerHTML =
        '<div class="row1"><span class="name">' + escapeHtml(conversation.title || '新对话') + '</span>' +
        '<span class="time">' + relativeTime(conversation.updatedAt) + '</span></div>' +
        '<div class="row2"><span class="ws">' + escapeHtml(baseName(conversation.cwd) || '未知目录') + '</span>' +
        (conversation.working ? '<span class="working"><span class="dot"></span>正在运行</span>' : '') +
        '</div>' +
        (conversation.snippet
          ? '<div class="row3">' + escapeHtml(conversation.snippet) + '</div>'
          : '');
      open.addEventListener('click', () => openSession(conversation));
      item.appendChild(open);

      const remove = document.createElement('button');
      remove.className = 'session-delete';
      remove.title = '删除对话';
      remove.textContent = '×';
      remove.addEventListener('click', (event) => {
        event.stopPropagation();
        confirmDelete(conversation);
      });
      item.appendChild(remove);

      el.sessionList.appendChild(item);
    }
  }
}

function confirmDelete(conversation) {
  closeModal();
  const card = document.createElement('div');
  card.className = 'modal';

  const title = document.createElement('h3');
  title.textContent = '删除这个对话';
  card.appendChild(title);

  const note = document.createElement('div');
  note.className = 'modal-message';
  note.textContent = '「' + (conversation.title || '未命名对话') + '」的历史会从这个列表消失。Pi 那边的原始记录不删。';
  card.appendChild(note);

  const actions = document.createElement('div');
  actions.className = 'modal-actions';

  const cancel = document.createElement('button');
  cancel.className = 'ghost-btn';
  cancel.textContent = '取消';
  cancel.addEventListener('click', closeModal);

  const remove = document.createElement('button');
  remove.className = 'primary-btn danger';
  remove.textContent = '删除';
  remove.addEventListener('click', () => {
    closeModal();
    send({ type: 'delete_conversation', id: conversation.id });
    if (app.currentKey === conversation.id) {
      app.currentKey = null;
      app.sessions = app.sessions.filter((item) => item.id !== conversation.id);
      showWorkspace(false);
      renderSessionList();
    }
  });

  actions.appendChild(cancel);
  actions.appendChild(remove);
  card.appendChild(actions);

  el.modal.innerHTML = '';
  el.modal.appendChild(card);
  el.modal.hidden = false;
}

/* ------------------------------------------------------------------ *
 * 渲染：线程
 * ------------------------------------------------------------------ */

function contentText(content) {
  if (typeof content === 'string') return content;
  if (Array.isArray(content)) {
    return content
      .filter((block) => block && block.type === 'text')
      .map((block) => block.text || '')
      .join('\n');
  }
  return '';
}

function contentImages(content) {
  if (!Array.isArray(content)) return [];
  return content
    .filter((block) => block && block.type === 'image' && block.data)
    .map((block) => 'data:' + (block.mimeType || 'image/png') + ';base64,' + block.data);
}

function turnsOf(session) {
  const turns = [];
  for (const item of session.items) {
    if (item.kind === 'user' || !turns.length) {
      turns.push({ items: [] });
    }
    turns[turns.length - 1].items.push(item);
  }
  return turns;
}

function userNode(item) {
  const wrapper = document.createElement('div');
  wrapper.className = 'msg user';
  wrapper.dataset.item = item.id;

  const bubble = document.createElement('div');
  bubble.className = 'bubble';
  const md = document.createElement('div');
  md.className = 'md';
  md.innerHTML = renderMarkdown(item.text);
  bubble.appendChild(md);

  if (item.images && item.images.length) {
    const wrap = document.createElement('div');
    wrap.className = 'images';
    for (const url of item.images) {
      const img = document.createElement('img');
      img.src = url;
      wrap.appendChild(img);
    }
    bubble.appendChild(wrap);
  }

  wrapper.appendChild(bubble);
  return wrapper;
}

/**
 * 一轮 = 一条用户消息 + 之后的所有助手动作。
 * 模型还在干活的时候把每一步都摆出来（一行一步，不展开）；
 * 正文一出现就把这些收成一行「工作过程」，让回答站在前面。
 */
function turnNode(turn, session, isLast) {
  const node = document.createElement('div');
  node.className = 'turn';

  const work = [];
  const texts = [];
  const images = [];

  for (const item of turn.items) {
    if (item.kind === 'user') {
      node.appendChild(userNode(item));
      continue;
    }

    for (const block of item.blocks || []) {
      if (block.type === 'text' && block.text) {
        texts.push(block.text);
      } else if (block.type === 'thinking' && block.text) {
        work.push({ kind: 'thinking', block, item });
      } else if (block.type === 'toolCall') {
        work.push({ kind: 'tool', block, item });
      } else if (block.type === 'image') {
        images.push(block);
      }
    }
  }

  const streaming = Boolean(session.streaming) && isLast;
  const answered = texts.length > 0;
  const hasWork = work.length > 0;

  const assistant = document.createElement('div');
  assistant.className = 'msg assistant';

  if (hasWork) {
    assistant.appendChild(workGroup(work, answered && !streaming, streaming));
  }

  for (const text of texts) {
    const md = document.createElement('div');
    md.className = 'md';
    md.innerHTML = renderMarkdown(text);
    assistant.appendChild(md);
  }

  for (const block of images) {
    const img = document.createElement('img');
    img.src = block.url;
    img.style.maxWidth = '280px';
    img.style.borderRadius = '8px';
    assistant.appendChild(img);
  }

  if (assistant.childElementCount) {
    node.appendChild(assistant);
  }

  return node;
}

function workGroup(work, collapsed, streaming) {
  const wrap = document.createElement('div');
  wrap.className = 'work';

  const tools = work.filter((entry) => entry.kind === 'tool').length;
  const metaText = [
    work.length + ' 步',
    tools ? tools + ' 次工具调用' : '只有思考',
  ].join(' · ');

  const head = document.createElement('button');
  head.className = 'work-head';
  head.innerHTML =
    '<span class="work-caret">▸</span>' +
    '<span class="work-title">' + (streaming && !collapsed ? '正在处理' : '工作过程') + '</span>' +
    '<span class="work-meta">' + escapeHtml(metaText) + '</span>';

  const body = document.createElement('div');
  body.className = 'work-body';

  for (const entry of work) {
    body.appendChild(stepRow(entry));
  }

  const setCollapsed = (value) => {
    wrap.classList.toggle('is-collapsed', value);
    body.hidden = value;
    head.querySelector('.work-caret').textContent = value ? '▸' : '▾';
  };

  setCollapsed(collapsed);
  head.addEventListener('click', () => setCollapsed(!wrap.classList.contains('is-collapsed')));

  wrap.appendChild(head);
  wrap.appendChild(body);
  return wrap;
}

/** 一行一步：思考或某个工具，点开才看细节。 */
function stepRow(entry) {
  const row = document.createElement('div');
  row.className = 'step' + (entry.kind === 'thinking' ? ' is-thinking' : '');

  const head = document.createElement('button');
  head.className = 'step-head';

  const label = entry.kind === 'thinking' ? '思考' : (entry.block.name || '工具');
  const summary = entry.kind === 'thinking'
    ? String(entry.block.text).replace(/\s+/g, ' ').slice(0, 90)
    : toolSummary(entry.block);

  const running = entry.kind === 'tool' && !entry.block.done;
  head.innerHTML =
    '<span class="step-dot' + (running ? ' is-running' : '') + '"></span>' +
    '<span class="step-name">' + escapeHtml(label) + '</span>' +
    '<span class="step-text">' + escapeHtml(summary) + '</span>' +
    (entry.block.durationMs != null
      ? '<span class="step-time">' + escapeHtml(formatDuration(entry.block.durationMs)) + '</span>'
      : '');

  row.appendChild(head);

  const detail = document.createElement('div');
  detail.className = 'step-detail';
  detail.hidden = true;

  if (entry.kind === 'thinking') {
    const pre = document.createElement('pre');
    pre.textContent = entry.block.text;
    detail.appendChild(pre);
  } else {
    detail.appendChild(toolCard(entry.block));
  }

  head.addEventListener('click', () => {
    detail.hidden = !detail.hidden;
    row.classList.toggle('is-open', !detail.hidden);
  });

  row.appendChild(detail);
  return row;
}

function formatDuration(ms) {
  return ms >= 1000 ? (ms / 1000).toFixed(1) + ' 秒' : Math.round(ms) + ' 毫秒';
}

function toolCard(block) {
  const card = document.createElement('div');
  card.className = 'tool' + (block.open ? ' open' : '');

  const head = document.createElement('div');
  head.className = 'tool-head';

  const state = document.createElement('span');
  state.className = 'state ' + (block.running ? 'running' : block.isError ? 'err' : block.done ? 'ok' : 'running');

  const name = document.createElement('span');
  name.className = 'tool-name';
  name.textContent = block.name || 'tool';

  const args = document.createElement('span');
  args.className = 'tool-args';
  args.textContent = toolSummary(block);

  // 工具读写的路径点一下就能在文件面板里看，省得自己去目录里翻。
  const target = block.args?.path || block.args?.file_path;
  if (typeof target === 'string' && target) {
    args.classList.add('is-link');
    args.title = '在文件面板里打开 ' + target;
    args.addEventListener('click', (event) => {
      event.stopPropagation();
      const session = current();
      if (!session) return;
      el.filePanel.hidden = false;
      document.querySelector('.main').classList.add('files-open');
      el.filesPath.textContent = '/' + target;
      el.filesPath.title = '/' + target;
      el.filesBody.innerHTML = '<div class="files-note">读取中…</div>';
      send({ type: 'read_file', key: session.key, path: target });
    });
  }

  head.appendChild(state);
  head.appendChild(name);
  head.appendChild(args);

  if (block.durationMs != null) {
    const time = document.createElement('span');
    time.className = 'tool-time';
    time.textContent = block.durationMs >= 1000
      ? (block.durationMs / 1000).toFixed(1) + ' 秒'
      : block.durationMs + ' 毫秒';
    head.appendChild(time);
  }

  head.addEventListener('click', () => {
    block.open = !block.open;
    card.classList.toggle('open', Boolean(block.open));
  });

  const body = document.createElement('div');
  body.className = 'tool-body';

  if (block.argsText) {
    const label = document.createElement('div');
    label.className = 'label';
    label.textContent = '参数';
    const pre = document.createElement('pre');
    pre.textContent = block.argsText;
    body.appendChild(label);
    body.appendChild(pre);
  }

  if (block.output) {
    const label = document.createElement('div');
    label.className = 'label';
    label.textContent = block.isError ? '输出（出错）' : '输出';
    const pre = document.createElement('pre');
    if (block.isError) pre.className = 'err-text';
    pre.textContent = block.output;
    body.appendChild(label);
    body.appendChild(pre);
  }

  card.appendChild(head);
  card.appendChild(body);
  return card;
}

function toolSummary(block) {
  const args = block.args || {};
  const pick = args.command || args.path || args.file_path || args.pattern || args.query || args.url;
  if (typeof pick === 'string') {
    return pick.length > 110 ? pick.slice(0, 110) + '…' : pick;
  }
  if (block.argsText) {
    const flat = block.argsText.replace(/\s+/g, ' ');
    return flat.length > 110 ? flat.slice(0, 110) + '…' : flat;
  }
  return '';
}

function renderThread() {
  const session = current();
  app.turnNodes = [];
  el.threadInner.innerHTML = '';
  if (!session) return;

  const turns = turnsOf(session);
  turns.forEach((turn, index) => {
    const node = turnNode(turn, session, index === turns.length - 1);
    app.turnNodes.push({ items: turn.items, node });
    el.threadInner.appendChild(node);
  });

  scrollToBottom(true);
}

function patchItem(item) {
  const session = current();
  if (!session) return;

  const turns = turnsOf(session);
  const index = turns.findIndex((turn) => turn.items.includes(item));
  if (index < 0) {
    renderThread();
    return;
  }

  const turn = turns[index];
  const isLast = index === turns.length - 1;
  const node = turnNode(turn, session, isLast);
  const known = app.turnNodes[index];

  if (known && known.node.isConnected) {
    known.node.replaceWith(node);
    known.node = node;
    known.items = turn.items;
  } else {
    renderThread();
    return;
  }
}

function atBottom() {
  return el.thread.scrollHeight - el.thread.scrollTop - el.thread.clientHeight < 90;
}

function scrollToBottom(force) {
  if (force || atBottom()) {
    el.thread.scrollTop = el.thread.scrollHeight;
  }
}

function schedule(itemId) {
  app.dirty.add(itemId);
  if (app.frame) return;
  app.frame = requestAnimationFrame(() => {
    app.frame = 0;
    const session = current();
    const pinned = atBottom();
    for (const id of app.dirty) {
      const item = session && session.items.find((candidate) => candidate.id === id);
      if (item) patchItem(item);
    }
    app.dirty.clear();
    if (pinned) scrollToBottom(true);
  });
}

/* ------------------------------------------------------------------ *
 * 渲染：顶部与输入区
 * ------------------------------------------------------------------ */

function renderHeader() {
  const session = current();
  if (!session) {
    el.sessionTitle.textContent = '新对话';
    el.sessionCwd.textContent = '';
    el.crumbSep.hidden = true;
    el.workspacePill.hidden = true;
    el.wsSelectText.textContent = baseName(app.lastCwd || app.activeCwd) || '选择工作区';
    el.workspacePill.title = app.lastCwd || app.activeCwd || '';
    el.modelSelect.hidden = true;
    el.thinkingSelect.hidden = true;
    el.compactBtn.hidden = true;
    el.ctxUsage.hidden = true;
    el.renameBtn.hidden = true;
    el.exportBtn.hidden = true;
    el.terminalBtn.hidden = true;
    el.filesBtn.hidden = true;
    return;
  }

  const state = session.state || {};
  const firstUser = session.items.find((item) => item.kind === 'user');
  const title = state.sessionName || (firstUser && firstUser.text ? firstUser.text.split('\n')[0] : '') || '新对话';
  el.sessionTitle.textContent = title;
  el.sessionTitle.title = title;
  el.sessionCwd.textContent = baseName(session.cwd) || '';
  el.sessionCwd.title = session.cwd || '';
  el.crumbSep.hidden = !session.cwd;
  el.workspacePill.hidden = false;
  el.workspacePillText.textContent = baseName(session.cwd) || '选择工作区';
  el.workspacePill.title = session.cwd || '';
  el.wsSelectText.textContent = baseName(session.cwd) || '选择工作区';

  // 新对话还没跟 Pi 打过交道时，先按模型列表的第一项显示。
  const model = state.model
    || ((session.models && session.models.models && session.models.models[0]) || null);
  if (model) {
    const list = (session.models && session.models.models) || [];
    if (list.length > 1) {
      const wanted = model.provider + '/' + model.id;
      const signature = list.map((entry) => entry.provider + '/' + entry.id).join('|');
      if (el.modelSelect.dataset.signature !== signature) {
        fillModelSelect(list);
        el.modelSelect.dataset.signature = signature;
      }
      if (state.model) {
        el.modelSelect.value = wanted;
        el.modelSelect.title = model.name && model.name !== model.id ? wanted + '  ' + model.name : wanted;
      } else {
        el.modelSelect.value = '';
        el.modelSelect.title = '还没定，跟着 Pi 的默认走';
      }
      el.modelSelect.hidden = false;
    } else {
      el.modelSelect.hidden = true;
    }
  }

  const levels = (session.levels && session.levels.levels) || [];
  if (levels.length > 1) {
    // 库里记的等级可能不在当前模型的可选范围（换过模型就会出现），照实显示出来。
    const wanted = state.thinkingLevel || levels[0];
    const values = levels.includes(wanted) ? levels : [...levels, wanted];
    if (el.thinkingSelect.dataset.values !== values.join(',')) {
      el.thinkingSelect.innerHTML = '';
      for (const level of values) {
        const option = document.createElement('option');
        option.value = level;
        option.textContent = level;
        el.thinkingSelect.appendChild(option);
      }
      el.thinkingSelect.dataset.values = values.join(',');
    }
    el.thinkingSelect.value = wanted;
    el.thinkingSelect.hidden = false;
  } else {
    el.thinkingSelect.hidden = true;
  }

  el.compactBtn.hidden = false;
  el.renameBtn.hidden = false;
  el.exportBtn.hidden = false;
  el.terminalBtn.hidden = false;
  el.filesBtn.hidden = false;
  renderContext();
}

function fillModelSelect(list) {
  // 同一个模型可能通过不同路由提供（magpie 的 auto / workbuddy / amd…），
  // 只显示名字会分不清是哪一条，所以按 provider 分组，名字后面补上路由。
  const groups = new Map();
  for (const entry of list) {
    const provider = entry.provider || '其它';
    if (!groups.has(provider)) groups.set(provider, []);
    groups.get(provider).push(entry);
  }

  el.modelSelect.innerHTML = '';
  const placeholder = document.createElement('option');
  placeholder.value = '';
  placeholder.textContent = '默认模型';
  el.modelSelect.appendChild(placeholder);

  for (const [provider, entries] of groups) {
    const group = document.createElement('optgroup');
    group.label = provider;
    for (const entry of entries) {
      const option = document.createElement('option');
      option.value = entry.provider + '/' + entry.id;
      option.textContent = modelLabel(entry);
      option.title = entry.provider + '/' + entry.id;
      group.appendChild(option);
    }
    el.modelSelect.appendChild(group);
  }
}

function modelLabel(entry) {
  const name = entry.name || entry.id;
  const segments = String(entry.id || '').split('/');
  if (segments.length < 2) {
    return name;
  }

  // id 带路由前缀（magpie 的 auto / workbuddy / amd…）。名字里已经写了路由就不再重复。
  const route = segments[0];
  return name.toLowerCase().includes(route.toLowerCase()) ? name : name + ' · ' + route;
}

function renderContext() {
  const session = current();
  const usage = session && session.stats && session.stats.contextUsage;
  if (!usage || usage.percent == null) {
    el.ctxUsage.hidden = true;
    return;
  }
  el.ctxUsage.hidden = false;
  const percent = Number(usage.percent).toFixed(1).replace(/\.0$/, '');
  const used = (usage.tokens / 1000).toFixed(0);
  const window = Math.round(usage.contextWindow / 1000);
  el.ctxUsage.textContent = percent + '% · ' + used + 'k/' + window + 'k';
  el.ctxUsage.title = '上下文用量';
}

function renderComposerState() {
  const session = current();
  const streaming = Boolean(session && session.streaming);
  el.sendBtn.textContent = streaming ? '■' : '↑';
  el.sendBtn.title = streaming ? '停止' : '发送';
  el.sendBtn.classList.toggle('stop', streaming);
  if (!streaming) el.hint.textContent = '';
}

function renderAttachments() {
  el.attachments.innerHTML = '';
  el.attachments.hidden = app.attachments.length === 0;
  app.attachments.forEach((attachment, index) => {
    const chip = document.createElement('div');
    chip.className = 'attachment';
    const img = document.createElement('img');
    img.src = attachment.dataUrl;
    const label = document.createElement('span');
    label.textContent = attachment.name || 'image';
    const remove = document.createElement('button');
    remove.textContent = '×';
    remove.addEventListener('click', () => {
      app.attachments.splice(index, 1);
      renderAttachments();
    });
    chip.appendChild(img);
    chip.appendChild(label);
    chip.appendChild(remove);
    el.attachments.appendChild(chip);
  });
}

function renderExtStatus() {
  el.extStatus.innerHTML = '';
  for (const [, text] of app.extStatus) {
    const clean = text.trim();
    if (!clean) continue;
    const chip = document.createElement('span');
    chip.textContent = clean;
    el.extStatus.appendChild(chip);
  }

  const lines = [];
  for (const [, widget] of app.widgets) {
    for (const line of widget) {
      const clean = stripAnsi(line).trim();
      if (clean) lines.push(clean);
    }
  }
  el.widgetStrip.textContent = lines.join('\n');
  el.widgetStrip.hidden = lines.length === 0;
}

function showWorkspace(hasSession) {
  el.thread.hidden = !hasSession;
  el.empty.hidden = hasSession;
  el.content.classList.toggle('is-empty', !hasSession);
}

/* ------------------------------------------------------------------ *
 * 会话流程
 * ------------------------------------------------------------------ */

function openSession(conversation) {
  if (!conversation || !conversation.id) return;
  const known = app.byKey.get(conversation.id);
  if (known) {
    app.currentKey = conversation.id;
    app.activeCwd = conversation.cwd || '';
    showWorkspace(true);
    renderThread();
    renderHeader();
    renderComposerState();
    renderSessionList();
    return;
  }

  // 历史直接从 MAStation 的库里读，不再为它起 Pi 进程，所以旧对话也是秒开。
  setOverlay('正在打开对话…');
  send({ type: 'open_conversation', id: conversation.id });
  app.pendingCwd = conversation.cwd || '';
}

function newSession() {
  askCwd();
}

/**
 * 选工作区用的目录选择器：目录列表由中心给（browse_dir），
 * 浏览器里也能用，不依赖桌面客户端的原生对话框。
 */
function openDirPicker(startPath, onPick) {
  closeModal();
  app.dirPicker = { onPick, path: startPath || '' };

  const card = document.createElement('div');
  card.className = 'modal dir-picker';

  const title = document.createElement('h3');
  title.textContent = '选一个目录当工作区';
  card.appendChild(title);

  const where = document.createElement('div');
  where.className = 'dir-where';
  card.appendChild(where);

  const list = document.createElement('div');
  list.className = 'dir-list';
  card.appendChild(list);

  const actions = document.createElement('div');
  actions.className = 'modal-actions';

  const cancel = document.createElement('button');
  cancel.className = 'ghost-btn';
  cancel.textContent = '取消';
  cancel.addEventListener('click', () => {
    app.dirPicker = null;
    closeModal();
  });

  const use = document.createElement('button');
  use.className = 'primary-btn';
  use.textContent = '就用这个目录';
  use.addEventListener('click', () => {
    const chosen = app.dirPicker && app.dirPicker.path;
    const callback = app.dirPicker && app.dirPicker.onPick;
    app.dirPicker = null;
    closeModal();
    if (chosen && callback) callback(chosen);
  });

  actions.appendChild(cancel);
  actions.appendChild(use);
  card.appendChild(actions);
  el.modal.appendChild(card);
  el.modal.hidden = false;

  app.dirPicker.where = where;
  app.dirPicker.list = list;
  app.dirPicker.use = use;
  renderDirListing({ path: '', parent: null, drives: [], dirs: [], error: '', pending: true });
  send({ type: 'browse_dir', path: startPath || '' });
}

function renderDirListing(payload) {
  const picker = app.dirPicker;
  if (!picker) return;

  picker.path = payload.path || '';
  picker.where.textContent = picker.path || '这台机器上的盘符';
  picker.use.disabled = !picker.path;

  const list = picker.list;
  list.innerHTML = '';

  if (payload.error) {
    const note = document.createElement('div');
    note.className = 'dir-empty';
    note.textContent = payload.error;
    list.appendChild(note);
    return;
  }

  if (payload.pending) {
    const note = document.createElement('div');
    note.className = 'dir-empty';
    note.textContent = '正在读目录…';
    list.appendChild(note);
    return;
  }

  if (payload.parent) {
    const up = document.createElement('button');
    up.className = 'dir-row dir-up';
    up.textContent = '.. 上一级';
    up.addEventListener('click', () => {
      send({ type: 'browse_dir', path: payload.parent });
    });
    list.appendChild(up);
  }

  const entries = picker.path ? payload.dirs || [] : payload.drives || [];
  if (!entries.length) {
    const note = document.createElement('div');
    note.className = 'dir-empty';
    note.textContent = picker.path ? '这个目录下面没有子目录' : '没有可用的盘符';
    list.appendChild(note);
    return;
  }

  entries.forEach((entry) => {
    const row = document.createElement('button');
    row.className = 'dir-row';
    row.title = entry.path;
    row.textContent = entry.name;
    row.addEventListener('click', () => {
      send({ type: 'browse_dir', path: entry.path });
    });
    list.appendChild(row);
  });
}

function askCwd() {
  closeModal();
  const card = document.createElement('div');
  card.className = 'modal';

  const title = document.createElement('h3');
  title.textContent = '在哪个工作区新建对话';
  card.appendChild(title);

  const note = document.createElement('div');
  note.className = 'modal-message';
  note.textContent = '填中心那台机器上的绝对路径，对话就绑定在这个目录下。';
  card.appendChild(note);

  const inputRow = document.createElement('div');
  inputRow.className = 'path-row';

  const input = document.createElement('input');
  input.type = 'text';
  input.placeholder = 'D:\work\project';
  input.value = app.cwdDraft || app.lastCwd || app.activeCwd || '';
  app.cwdDraft = null;
  inputRow.appendChild(input);

  const browse = document.createElement('button');
  browse.className = 'ghost-btn';
  browse.textContent = '浏览…';
  browse.addEventListener('click', () => {
    openDirPicker(input.value, (picked) => {
      app.cwdDraft = picked;
      askCwd();
    });
  });
  inputRow.appendChild(browse);

  card.appendChild(inputRow);

  const recent = [...new Set([...(app.recentWorkspaces || []), ...app.sessions.map((session) => session.cwd)])]
    .filter(Boolean)
    .slice(0, 6);
  if (recent.length) {
    const list = document.createElement('div');
    list.className = 'options';
    for (const cwd of recent) {
      const option = document.createElement('button');
      option.className = 'option';
      option.textContent = cwd;
      option.addEventListener('click', () => {
        input.value = cwd;
        input.focus();
      });
      list.appendChild(option);
    }
    card.appendChild(list);
  }

  const check = document.createElement('div');
  check.className = 'modal-hint';
  card.appendChild(check);

  const checkPath = (value) => {
    if (!value) {
      check.textContent = '';
      return;
    }

    app.pathCheck = value;
    send({ type: 'check_path', path: value });
  };
  let checkTimer = 0;
  input.addEventListener('input', () => {
    window.clearTimeout(checkTimer);
    const value = input.value.trim();
    checkTimer = window.setTimeout(() => checkPath(value), 260);
  });
  checkPath(input.value.trim());

  const actions = document.createElement('div');
  actions.className = 'modal-actions';
  const cancel = document.createElement('button');
  cancel.className = 'ghost-btn';
  cancel.textContent = '取消';
  cancel.addEventListener('click', closeModal);
  const start = document.createElement('button');
  start.className = 'primary-btn';
  start.textContent = '开始';
  start.addEventListener('click', () => startSession(input.value.trim()));
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') startSession(input.value.trim());
  });
  actions.appendChild(cancel);
  actions.appendChild(start);
  card.appendChild(actions);

  el.modal.innerHTML = '';
  el.modal.appendChild(card);
  el.modal.hidden = false;
  setTimeout(() => input.focus(), 0);
}

function startSession(cwd) {
  if (!cwd) return;
  closeModal();
  setOverlay('正在打开对话…');
  pendingCwd = cwd;
  app.lastCwd = cwd;
  if (!send({ type: 'new_conversation', cwd })) {
    setOverlay(null);
    toast('还没连上中心，等连接恢复再试。', 'warning');
  }
}

/* 侧栏：折叠状态记在本地，重开还是上次的样子 */
function setSidebar(open) {
  el.windowEl.classList.toggle('sidebar-collapsed', !open);
  el.sidebarToggle.hidden = open;
  try {
    localStorage.setItem('piagentstation.sidebar', open ? '1' : '0');
  } catch {
    // 无痕模式写不了，忽略。
  }
}

el.collapseBtn.addEventListener('click', () => setSidebar(false));
el.sidebarToggle.addEventListener('click', () => setSidebar(true));

let sidebarOpen = true;
try {
  sidebarOpen = localStorage.getItem('piagentstation.sidebar') !== '0';
} catch {
  sidebarOpen = true;
}
setSidebar(sidebarOpen);

el.filterToggle.addEventListener('click', () => {
  el.filterbar.hidden = !el.filterbar.hidden;
  if (!el.filterbar.hidden) el.sessionFilter.focus();
});

el.workspacePill.addEventListener('click', () => {
  // 已有对话时工作区是定下来的，只有新对话才能挑。
  if (!current()) askCwd();
});

el.settingsBtn.addEventListener('click', openSettings);

function openSettings() {
  closeModal();
  const card = document.createElement('div');
  card.className = 'modal';

  const title = document.createElement('h3');
  title.textContent = '设置';
  card.appendChild(title);

  const rows = [
    ['中心', location.origin],
    ['版本', app.centerVersion || '未知'],
    ['对话库', app.storePath || '未知'],
    ['连接', transport.connected ? '已连接' : '未连接'],
    ['监听', (app.listen || '未知') + (/127\.0\.0\.1|localhost/.test(app.listen || '') ? '（只本机能连）' : '（局域网可连）')],
    ['Pi', app.pi ? (app.pi.ready ? '已装好 · ' + app.pi.agentDir : '没找到 · ' + app.pi.agentDir) : '未知'],
    ['会话目录', app.pi?.sessions || '未知'],
    ['最近工作区', (app.recentWorkspaces || []).join('、') || '还没有'],
  ];

  for (const [label, value] of rows) {
    const row = document.createElement('div');
    row.className = 'modal-message';
    row.innerHTML = '<span class="label">' + escapeHtml(label) + '</span><br>' + escapeHtml(value);
    card.appendChild(row);
  }

  const shortcuts = document.createElement('div');
  shortcuts.className = 'modal-message';
  shortcuts.innerHTML = '<span class="label">快捷键</span><br>' +
    ['Ctrl+K 新建对话', 'Ctrl+B 收起侧栏', 'Ctrl+J 终端', 'Ctrl+E 文件面板', 'Ctrl+F 搜索', 'Esc 关掉弹层']
      .map((line) => escapeHtml(line)).join('<br>');
  card.appendChild(shortcuts);

  const styleBlock = document.createElement('div');
  styleBlock.className = 'modal-message';
  styleBlock.innerHTML = '<span class="label">自定义样式</span><br>' +
    '<span class="hint">这段 CSS 贴在页面最后，保存后所有设备一起生效。</span>';

  const editor = document.createElement('textarea');
  editor.className = 'css-editor';
  editor.spellcheck = false;
  editor.value = app.customCss || DEFAULT_CSS;
  styleBlock.appendChild(editor);

  const styleBar = document.createElement('div');
  styleBar.className = 'css-actions';

  const saveStyle = document.createElement('button');
  saveStyle.className = 'ghost-btn small';
  saveStyle.textContent = '保存';
  saveStyle.addEventListener('click', () => {
    send({ type: 'set_css', css: editor.value });
    applyCustomCss(editor.value);
    toast('样式已保存');
  });

  const resetStyle = document.createElement('button');
  resetStyle.className = 'ghost-btn small';
  resetStyle.textContent = '恢复默认';
  resetStyle.addEventListener('click', () => {
    editor.value = DEFAULT_CSS;
    send({ type: 'set_css', css: DEFAULT_CSS });
    applyCustomCss(DEFAULT_CSS);
    toast('已恢复默认样式');
  });

  const clearStyle = document.createElement('button');
  clearStyle.className = 'ghost-btn small';
  clearStyle.textContent = '清空';
  clearStyle.addEventListener('click', () => {
    editor.value = '';
    send({ type: 'set_css', css: '' });
    applyCustomCss('');
    toast('已清空，回到默认样式');
  });

  styleBar.appendChild(saveStyle);
  styleBar.appendChild(resetStyle);
  styleBar.appendChild(clearStyle);
  styleBlock.appendChild(styleBar);
  card.appendChild(styleBlock);

  const actions = document.createElement('div');
  actions.className = 'modal-actions';

  const logButton = document.createElement('button');
  logButton.className = 'ghost-btn';
  logButton.textContent = '看最近日志';
  logButton.addEventListener('click', () => send({ type: 'read_log', lines: 120 }));
  actions.appendChild(logButton);

  const close = document.createElement('button');
  close.className = 'primary-btn';
  close.textContent = '知道了';
  close.addEventListener('click', closeModal);
  actions.appendChild(close);
  card.appendChild(actions);

  el.modal.innerHTML = '';
  el.modal.appendChild(card);
  el.modal.hidden = false;
}

el.newSessionBtn.addEventListener('click', newSession);
el.emptyNewBtn.addEventListener('click', newSession);
el.renameBtn.addEventListener('click', renameConversation);
el.sessionTitle.addEventListener('dblclick', renameConversation);

function renameConversation() {
  const session = current();
  if (!session) return;

  closeModal();
  const card = document.createElement('div');
  card.className = 'modal';

  const title = document.createElement('h3');
  title.textContent = '给这个对话改个名字';
  card.appendChild(title);

  const input = document.createElement('input');
  input.type = 'text';
  input.value = (session.state && session.state.sessionName) || '';
  card.appendChild(input);

  const actions = document.createElement('div');
  actions.className = 'modal-actions';

  const cancel = document.createElement('button');
  cancel.className = 'ghost-btn';
  cancel.textContent = '取消';
  cancel.addEventListener('click', closeModal);

  const save = document.createElement('button');
  save.className = 'primary-btn';
  save.textContent = '保存';
  const commit = () => {
    const next = input.value.trim();
    if (!next) {
      toast('名字不能是空的。', 'warning');
      return;
    }
    closeModal();
    send({ type: 'rename_conversation', id: session.key, title: next });
  };
  save.addEventListener('click', commit);
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') commit();
  });

  actions.appendChild(cancel);
  actions.appendChild(save);
  card.appendChild(actions);

  el.modal.innerHTML = '';
  el.modal.appendChild(card);
  el.modal.hidden = false;
  setTimeout(() => {
    input.focus();
    input.select();
  }, 0);
}

let searchTimer = 0;
el.sessionFilter.addEventListener('input', () => {
  app.filter = el.sessionFilter.value;
  renderSessionList();

  window.clearTimeout(searchTimer);
  const query = app.filter.trim();
  if (!query) {
    app.searchResults = null;
    return;
  }

  searchTimer = window.setTimeout(() => send({ type: 'search', query }), 240);
});

el.modelSelect.addEventListener('change', () => {
  const session = current();
  if (!session) return;
  if (!el.modelSelect.value) return;
  const [provider, ...rest] = el.modelSelect.value.split('/');
  send({ type: 'set_model', key: session.key, provider, modelId: rest.join('/') });
});

el.thinkingSelect.addEventListener('change', () => {
  const session = current();
  if (!session) return;
  send({ type: 'set_thinking', key: session.key, level: el.thinkingSelect.value });
});

el.compactBtn.addEventListener('click', () => {
  const session = current();
  if (!session) return;
  send({ type: 'compact', key: session.key });
  toast('正在压缩上下文');
});

/* ------------------------------------------------------------------ *
 * 输入与发送
 * ------------------------------------------------------------------ */

function autoGrow() {
  el.input.style.height = 'auto';
  el.input.style.height = Math.min(el.input.scrollHeight, 220) + 'px';
}

el.input.addEventListener('input', () => {
  autoGrow();
  queueCompletion();
});

el.input.addEventListener('keydown', (event) => {
  if (completionOpen()) {
    if (event.key === 'ArrowDown') { event.preventDefault(); moveCompletion(1); return; }
    if (event.key === 'ArrowUp') { event.preventDefault(); moveCompletion(-1); return; }
    if (event.key === 'Tab' || (event.key === 'Enter' && !event.shiftKey)) {
      event.preventDefault();
      acceptCompletion();
      return;
    }
    if (event.key === 'Escape') { event.preventDefault(); closeCompletion(); return; }
  }

  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    submitPrompt();
  }
});

el.input.addEventListener('blur', () => setTimeout(closeCompletion, 120));

document.addEventListener('keydown', (event) => {
  if (!event.ctrlKey || event.altKey || event.metaKey) return;
  const key = event.key.toLowerCase();

  // 在输入框里也要能用，这几个组合不跟打字冲突。
  if (key === 'k') {
    event.preventDefault();
    askCwd();
    return;
  }

  if (key === 'b') {
    event.preventDefault();
    el.collapseBtn.click();
    return;
  }

  if (key === 'j') {
    event.preventDefault();
    if (current()) showTerminal(el.terminalPanel.hidden);
    return;
  }

  if (key === 'e') {
    event.preventDefault();
    if (current()) {
      if (el.filePanel.hidden) openFiles(current().filesPath || '');
      else closeFiles();
    }
    return;
  }

  if (key === 'f' && current()) {
    event.preventDefault();
    el.filterToggle.click();
    el.sessionFilter.focus();
    el.sessionFilter.select();
  }
});

/* ------------------------------------------------------------------ *
 * 输入补全：@ 找文件，/ 找命令
 * ------------------------------------------------------------------ */

const completion = { kind: '', items: [], index: 0, start: 0, end: 0, query: '' };
let completionTimer = 0;

function completionOpen() {
  return !el.completions.hidden && completion.items.length > 0;
}

function applyCustomCss(css) {
  let node = document.getElementById('customCss');
  if (!node) {
    node = document.createElement('style');
    node.id = 'customCss';
    document.head.appendChild(node);
  }

  node.textContent = css || DEFAULT_CSS;
  app.customCss = css || '';
}

function closeCompletion() {
  completion.items = [];
  completion.kind = '';
  el.completions.hidden = true;
  el.completions.innerHTML = '';
}

function queueCompletion() {
  const value = el.input.value;
  const caret = el.input.selectionStart ?? value.length;
  const before = value.slice(0, caret);

  if (value.startsWith('/') && !value.slice(0, caret).includes(' ')) {
    requestCommands(before.slice(1));
    return;
  }

  const match = /(^|\s)@([^\s@]*)$/.exec(before);
  const session = current();
  if (!match || !session) {
    closeCompletion();
    return;
  }

  const query = match[2];
  completion.kind = 'file';
  completion.start = caret - query.length - 1;
  completion.end = caret;
  completion.query = query;

  window.clearTimeout(completionTimer);
  completionTimer = window.setTimeout(() => {
    send({ type: 'complete_files', key: session.key, query });
  }, 120);
}

function requestCommands(prefix) {
  const session = current();
  if (!session) {
    closeCompletion();
    return;
  }

  completion.kind = 'command';
  completion.start = 0;
  completion.end = (el.input.selectionStart ?? prefix.length + 1);
  completion.query = prefix;

  const items = (session.commands || []).filter((command) =>
    String(command.name || '').toLowerCase().startsWith(prefix.toLowerCase()));
  renderCompletions(items);

  if (!session.commands) {
    send({ type: 'list_commands', key: session.key });
  }
}

function renderCompletions(items) {
  completion.items = items.slice(0, 60);
  completion.index = 0;

  if (!completion.items.length) {
    el.completions.innerHTML =
      '<div class="empty-hint">' +
      (completion.kind === 'command' ? '这里没有匹配的命令' : '没有匹配的文件') +
      '</div>';
    el.completions.hidden = false;
    return;
  }

  el.completions.innerHTML = '';
  completion.items.forEach((item, index) => {
    const row = document.createElement('button');
    row.className = 'completion-item' + (index === 0 ? ' is-active' : '');

    if (completion.kind === 'file') {
      row.innerHTML =
        '<span class="path">' + escapeHtml(item.path) + '</span>' +
        '<span class="kind">' + (item.dir ? '目录' : '文件') + '</span>';
    } else {
      row.innerHTML =
        '<span class="path">/' + escapeHtml(item.name || '') + '</span>' +
        '<span class="desc">' + escapeHtml(item.description || '') + '</span>' +
        '<span class="kind">' + escapeHtml(item.source || '') + '</span>';
    }

    row.addEventListener('mousedown', (event) => {
      event.preventDefault();
      completion.index = index;
      acceptCompletion();
    });
    el.completions.appendChild(row);
  });

  el.completions.hidden = false;
}

function moveCompletion(step) {
  const count = completion.items.length;
  if (!count) return;
  completion.index = (completion.index + step + count) % count;
  [...el.completions.querySelectorAll('.completion-item')].forEach((node, index) => {
    node.classList.toggle('is-active', index === completion.index);
    if (index === completion.index) node.scrollIntoView({ block: 'nearest' });
  });
}

function acceptCompletion() {
  const item = completion.items[completion.index];
  if (!item) {
    closeCompletion();
    return;
  }

  const value = el.input.value;
  const before = value.slice(0, completion.start);
  const after = value.slice(completion.end);
  const insert = completion.kind === 'file' ? '@' + item.path : '/' + item.name + ' ';

  el.input.value = before + insert + after;
  const caret = (before + insert).length;
  el.input.setSelectionRange(caret, caret);
  el.input.focus();
  closeCompletion();
  autoGrow();

  // 补完目录接着补里面的内容，省得再打一次 @。
  if (item.dir) {
    queueCompletion();
  }
}

el.sendBtn.addEventListener('click', () => {
  const session = current();
  if (session && session.streaming) {
    send({ type: 'abort', key: session.key });
    toast('已请求停止');
    return;
  }
  submitPrompt();
});

function exportMarkdown() {
  const session = current();
  if (!session) return;

  const title = (session.state && session.state.sessionName) || '对话';
  const lines = [
    '# ' + title,
    '',
    '- 工作区：`' + (session.cwd || '') + '`',
    '- 导出时间：' + new Date().toLocaleString('zh-CN'),
    '',
  ];

  for (const item of session.items) {
    if (item.kind === 'user') {
      lines.push('## 我', '', item.text || '（图片）', '');
      continue;
    }

    if (item.kind !== 'assistant') continue;

    const text = item.blocks
      .filter((block) => block.type === 'text')
      .map((block) => block.text)
      .join('\n\n')
      .trim();
    if (text) {
      lines.push('## 助手', '', text, '');
    }

    for (const block of item.blocks.filter((entry) => entry.type === 'toolCall')) {
      lines.push('> `' + block.name + '` ' + toolSummary(block), '');
    }
  }

  const blob = new Blob([lines.join('\n')], { type: 'text/markdown;charset=utf-8' });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = title.replace(/[\\/:*?"<>|]/g, '_').slice(0, 60) + '.md';
  link.click();
  URL.revokeObjectURL(link.href);
  toast('已导出为 Markdown');
}

el.exportBtn.addEventListener('click', exportMarkdown);

/* ------------------------------------------------------------------ *
 * 终端：每条对话一个常驻 shell，输出是中心广播过来的
 * ------------------------------------------------------------------ */

const terminal = { history: [], cursor: -1 };

function terminalText(session) {
  if (!session) return '';
  if (session.terminalText === undefined) session.terminalText = '';
  return session.terminalText;
}

function appendTerminal(text) {
  const session = current();
  if (!session) return;

  const body = el.terminalBody;
  const atBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 24;

  session.terminalText = (terminalText(session) + text).slice(-200000);
  body.textContent = session.terminalText;
  body.scrollTop = atBottom ? body.scrollHeight : body.scrollTop;
}

function showTerminal(visible) {
  el.terminalPanel.hidden = !visible;
  if (!visible) return;

  const session = current();
  if (session) {
    el.terminalBody.textContent = terminalText(session);
    el.terminalBody.scrollTop = el.terminalBody.scrollHeight;
    send({ type: 'terminal_start', key: session.key });
  }

  el.terminalInput.focus();
}

function runTerminalLine() {
  const session = current();
  const line = el.terminalInput.value;
  if (!session || !line.trim()) return;

  terminal.history.push(line);
  terminal.cursor = terminal.history.length;
  el.terminalInput.value = '';

  // PowerShell 读标准输入时不会回显命令，界面上自己补一行。
  appendTerminal('> ' + line + '\n');
  send({ type: 'terminal_input', key: session.key, text: line });
}

el.terminalBtn.addEventListener('click', () => showTerminal(el.terminalPanel.hidden));

/* ------------------------------------------------------------------ *
 * 工作区文件：只读浏览与预览
 * ------------------------------------------------------------------ */

function openFiles(path) {
  const session = current();
  if (!session) return;

  el.filePanel.hidden = false;
  document.querySelector('.main').classList.add('files-open');
  el.filesPath.textContent = '/' + path;
  el.filesPath.title = '/' + path;
  el.filesBody.innerHTML = '<div class="files-note">读取中…</div>';
  session.filesPath = path;
  send({ type: 'list_dir', key: session.key, path });
}

function formatSize(bytes) {
  if (!bytes) return '';
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return Math.round(bytes / 1024) + ' KB';
  return (bytes / 1024 / 1024).toFixed(1) + ' MB';
}

function renderListing(message) {
  el.filesBody.innerHTML = '';

  if (!message.exists) {
    el.filesBody.innerHTML = '<div class="files-note">这里没有东西，或者已经不在工作区里了。</div>';
    return;
  }

  if (!message.items.length) {
    el.filesBody.innerHTML = '<div class="files-note">空目录</div>';
    return;
  }

  for (const item of message.items) {
    const row = document.createElement('button');
    row.className = 'files-row';
    row.innerHTML =
      '<span class="mark">' + (item.dir ? '▸' : '·') + '</span>' +
      '<span class="name">' + escapeHtml(item.name) + '</span>' +
      '<span class="meta">' + (item.dir ? '' : escapeHtml(formatSize(item.size))) + '</span>';

    row.addEventListener('click', () => {
      if (item.dir) {
        openFiles(item.path);
        return;
      }

      const session = current();
      if (!session) return;
      el.filesPath.textContent = '/' + item.path;
      el.filesBody.innerHTML = '<div class="files-note">读取中…</div>';
      send({ type: 'read_file', key: session.key, path: item.path });
    });

    el.filesBody.appendChild(row);
  }
}

function renderFileContent(message) {
  el.filesBody.innerHTML = '';

  if (message.text === null || message.text === undefined) {
    const note = document.createElement('div');
    note.className = 'files-note';
    note.textContent = message.reason || '打不开这个文件';
    el.filesBody.appendChild(note);
    return;
  }

  const pre = document.createElement('pre');
  pre.className = 'files-preview';
  pre.textContent = message.text;
  el.filesBody.appendChild(pre);
  el.filesBody.scrollTop = 0;
}

el.filesBtn.addEventListener('click', () => {
  if (!el.filePanel.hidden) {
    closeFiles();
    return;
  }

  const session = current();
  openFiles(session?.filesPath || '');
});

el.filesClose.addEventListener('click', () => closeFiles());

function closeFiles() {
  el.filePanel.hidden = true;
  document.querySelector('.main').classList.remove('files-open');
}

el.filesUp.addEventListener('click', () => {
  const session = current();
  if (!session) return;
  const parts = (session.filesPath || '').split('/').filter(Boolean);
  parts.pop();
  openFiles(parts.join('/'));
});
el.terminalClose.addEventListener('click', () => showTerminal(false));
el.terminalClear.addEventListener('click', () => {
  const session = current();
  if (session) session.terminalText = '';
  el.terminalBody.textContent = '';
});

el.terminalStop.addEventListener('click', () => {
  const session = current();
  if (session) send({ type: 'terminal_stop', key: session.key });
});

el.terminalInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    runTerminalLine();
    return;
  }

  if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
    if (!terminal.history.length) return;
    event.preventDefault();
    terminal.cursor = Math.max(0, Math.min(terminal.history.length, terminal.cursor + (event.key === 'ArrowUp' ? -1 : 1)));
    el.terminalInput.value = terminal.cursor === terminal.history.length ? '' : terminal.history[terminal.cursor];
    return;
  }

  if (event.key === 'Escape') {
    event.preventDefault();
    showTerminal(false);
  }
});

function submitPrompt() {
  const text = el.input.value.trim();
  if (!text) return;

  const session = current();
  if (!session) {
    // 还没有对话时，直接用当前工作区建一个，再把这句话发出去。
    const cwd = app.lastCwd || app.activeCwd;
    if (!cwd) {
      askCwd();
      return;
    }

    app.pendingPrompt = text;
    el.input.value = '';
    autoGrow();
    startSession(cwd);
    return;
  }

  const message = {
    type: 'prompt',
    key: session.key,
    message: text,
  };

  if (app.attachments.length) {
    message.images = app.attachments.map((attachment) => ({
      type: 'image',
      data: attachment.data,
      mimeType: attachment.mimeType,
    }));
  }

  if (session.streaming) {
    message.streamingBehavior = 'steer';
  }

  send(message);
  el.input.value = '';
  app.attachments = [];
  renderAttachments();
  autoGrow();
}

el.attachBtn.addEventListener('click', () => el.fileInput.click());

el.fileInput.addEventListener('change', async () => {
  const files = [...el.fileInput.files];
  el.fileInput.value = '';
  await addImageFiles(files);
});

/* 图片可以直接粘、也可以拖进窗口 */
document.addEventListener('click', (event) => {
  if (event.target.matches('.msg .md img, .attachment img')) {
    const overlay = document.createElement('div');
    overlay.className = 'image-zoom';
    const image = document.createElement('img');
    image.src = event.target.src;
    overlay.appendChild(image);
    overlay.addEventListener('click', () => overlay.remove());
    document.body.appendChild(overlay);
    event.preventDefault();
    return;
  }

  if (event.target.classList.contains('image-zoom')) {
    event.target.remove();
  }
});

document.addEventListener('click', async (event) => {
  const button = event.target.closest('.code-copy');
  if (!button) return;
  event.preventDefault();
  const code = decodeURIComponent(button.dataset.code || '');

  try {
    await navigator.clipboard.writeText(code);
  } catch {
    const area = document.createElement('textarea');
    area.value = code;
    document.body.appendChild(area);
    area.select();
    document.execCommand('copy');
    area.remove();
  }

  button.textContent = '已复制';
  window.setTimeout(() => { button.textContent = '复制'; }, 1200);
});

el.content.addEventListener('scroll', () => {
  const distance = el.content.scrollHeight - el.content.scrollTop - el.content.clientHeight;
  el.jumpBottom.hidden = distance < 200;
});

el.jumpBottom.addEventListener('click', () => {
  el.content.scrollTo({ top: el.content.scrollHeight, behavior: 'smooth' });
});

document.addEventListener('visibilitychange', () => {
  if (document.hidden) return;
  // 别的设备可能改过东西，切回来时对一下。
  send({ type: 'list_conversations' });
  const session = current();
  if (session) send({ type: 'refresh_state', key: session.key });
});

document.addEventListener('paste', (event) => {
  const files = [...(event.clipboardData?.files || [])].filter((file) => file.type.startsWith('image/'));
  if (!files.length) return;
  event.preventDefault();
  addImageFiles(files);
});

let dragDepth = 0;
document.addEventListener('dragenter', (event) => {
  if (![...(event.dataTransfer?.types || [])].includes('Files')) return;
  dragDepth += 1;
  document.body.classList.add('dragging');
});

document.addEventListener('dragleave', () => {
  dragDepth = Math.max(0, dragDepth - 1);
  if (!dragDepth) document.body.classList.remove('dragging');
});

document.addEventListener('dragover', (event) => {
  if ([...(event.dataTransfer?.types || [])].includes('Files')) event.preventDefault();
});

document.addEventListener('drop', async (event) => {
  const files = [...(event.dataTransfer?.files || [])];
  if (!files.length) return;
  event.preventDefault();
  dragDepth = 0;
  document.body.classList.remove('dragging');
  await addImageFiles(files);
});

/**
 * 截图动辄 4K，原样塞进去对话会变得很重。超过 1600 像素就等比缩一下，
 * 透明图仍存 PNG，其它转 JPEG。
 */
async function prepareImage(file) {
  const dataUrl = await new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });

  const mimeType = file.type || 'image/png';
  if (mimeType === 'image/gif') {
    return { dataUrl, mimeType };
  }

  const image = await new Promise((resolve) => {
    const probe = new Image();
    probe.onload = () => resolve(probe);
    probe.onerror = () => resolve(null);
    probe.src = dataUrl;
  });

  if (!image) {
    return { dataUrl, mimeType };
  }

  const longest = Math.max(image.width, image.height);
  if (longest <= 1600 && file.size < 1.5 * 1024 * 1024) {
    return { dataUrl, mimeType };
  }

  const scale = Math.min(1, 1600 / longest);
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(image.width * scale);
  canvas.height = Math.round(image.height * scale);
  canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);

  const output = mimeType === 'image/png' ? 'image/png' : 'image/jpeg';
  return { dataUrl: canvas.toDataURL(output, 0.9), mimeType: output };
}

async function addImageFiles(files) {
  const images = files.filter((file) => file.type.startsWith('image/'));
  if (!images.length) {
    if (files.length) toast('现在只能附加图片。', 'warning');
    return;
  }

  for (const file of images) {
    const prepared = await prepareImage(file);
    const comma = prepared.dataUrl.indexOf(',');
    app.attachments.push({
      name: file.name || '粘贴的图片',
      mimeType: prepared.mimeType,
      data: prepared.dataUrl.slice(comma + 1),
      dataUrl: prepared.dataUrl,
    });
  }

  renderAttachments();
  el.input.focus();
}

/* ------------------------------------------------------------------ *
 * 宿主消息分发
 * ------------------------------------------------------------------ */

let pendingCwd = '';

function dispatch(message) {
  switch (message.type) {
    case 'hello':
      if (message.center) {
        app.centerName = message.center;
        if (transport.connected) setConnectionState('已连接', true);
      }
      if (message.data) {
        app.storePath = message.data;
      }
      if (message.version) {
        app.centerVersion = message.version;
      }
      if (message.pi) {
        app.pi = message.pi;
      }
      if (message.listen) {
        app.listen = message.listen;
      }
      if (message.css !== undefined) {
        applyCustomCss(message.css || '');
      }
      if (Array.isArray(message.workspaces)) {
        app.recentWorkspaces = message.workspaces;
      }
      if (message.pi && message.pi.ready === false) {
        toast('中心那台机器上没找到 Pi，先把 Pi 装好再开始对话。', 'warning');
      }
      break;

    case 'path_check': {
      // 只提示，不拦人：中心那边建对话时本来就会校验目录。
      const hint = el.modal.querySelector('.modal-hint');
      if (hint) {
        hint.textContent = message.exists ? '' : message.reason || '';
      }
      break;
    }

    case 'browse_dir':
      renderDirListing(message);
      break;

    case 'css':
      applyCustomCss(message.css || '');
      break;

    case 'log_tail': {
      const card = document.createElement('div');
      card.className = 'modal wide';
      card.innerHTML = '<h3>中心日志</h3><div class="modal-message">' + escapeHtml(message.path || '') + '</div>';
      const pre = document.createElement('pre');
      pre.className = 'log-tail';
      pre.textContent = message.text || '（空）';
      card.appendChild(pre);
      const actions = document.createElement('div');
      actions.className = 'modal-actions';
      const close = document.createElement('button');
      close.className = 'primary-btn';
      close.textContent = '知道了';
      close.addEventListener('click', closeModal);
      actions.appendChild(close);
      card.appendChild(actions);
      el.modal.innerHTML = '';
      el.modal.appendChild(card);
      el.modal.hidden = false;
      break;
    }

    case 'search_results':
      if (message.query !== app.filter.trim()) break;
      app.searchResults = { query: message.query, items: message.items || [] };
      renderSessionList();
      break;

    case 'conversation_list':
      app.sessions = message.items || [];
      if (Array.isArray(message.workspaces)) {
        app.recentWorkspaces = message.workspaces;
      }
      renderSessionList();
      break;

    case 'conversation_saved':
      send({ type: 'list_conversations' });
      break;

    case 'conversation_settings': {
      const session = app.byKey.get(message.key);
      if (session) {
        if (typeof message.title === 'string' && message.title) {
          session.state.sessionName = message.title;
        }
        if (message.model) session.state.model = message.model;
        if (message.thinkingLevel) session.state.thinkingLevel = message.thinkingLevel;
      }
      const entry = app.sessions.find((item) => item.id === message.key);
      if (entry && message.title) entry.title = message.title;
      if (message.key === app.currentKey) renderHeader();
      renderSessionList();
      break;
    }


    case 'session_state':
      onSessionState(message);
      break;

    case 'dir_listing': {
      if (current()?.key !== message.key) break;
      if (el.filePanel.hidden) break;
      renderListing(message);
      break;
    }

    case 'file_content': {
      if (current()?.key !== message.key) break;
      if (el.filePanel.hidden) break;
      renderFileContent(message);
      break;
    }

    case 'terminal_state': {
      const session = app.byKey.get(message.key);
      if (!session) break;
      session.terminalShell = message.shell || '';
      session.terminalRunning = Boolean(message.running);
      if (current() === session && !el.terminalPanel.hidden) {
        el.terminalShell.textContent = [session.terminalShell, message.cwd].filter(Boolean).join(' · ');
      }
      break;
    }

    case 'terminal_output': {
      if (current()?.key !== message.key) break;
      appendTerminal(message.text || '');
      break;
    }

    case 'terminal_exit': {
      if (current()?.key !== message.key) break;
      appendTerminal('[进程已结束，退出码 ' + message.code + ']\n');
    }

    case 'file_completions': {
      const session = app.byKey.get(message.key);
      if (!session || completion.kind !== 'file') break;
      // 只认最后一次请求，慢回来的结果丢掉。
      if (message.query !== completion.query) break;
      renderCompletions(message.items || []);
      break;
    }

    case 'commands': {
      const session = app.byKey.get(message.key);
      if (!session) break;
      session.commands = message.items || [];
      if (completion.kind === 'command') requestCommands(completion.query);
      break;
    }

    case 'models':
      onModels(message);
      break;

    case 'stats': {
      const session = app.byKey.get(message.key);
      if (session) {
        session.stats = message.data || null;
        if (message.key === app.currentKey) renderContext();
      }
      break;
    }

    case 'event':
      onEvent(message.key, message.event);
      break;

    case 'prompt_accepted':
      break;

    case 'process_exit':
      onProcessExit(message);
      break;

    case 'error':
      setOverlay(null);
      toast(message.message || '这条操作没成功，可以再试一次。', 'error');
      break;

    default:
      break;
  }
}

function onSessionState(message) {
  setOverlay(null);
  const key = message.key;
  const previous = app.byKey.get(key);
  const cwd = message.cwd || (previous && previous.cwd) || pendingCwd || app.pendingCwd || '';
  const session = makeSession(key, message.state, message.messages, cwd);
  session.stats = message.stats || null;
  if (previous) {
    session.models = previous.models;
    session.levels = previous.levels;
  }

  app.byKey.set(key, session);
  app.currentKey = key;
  app.activeCwd = cwd;
  pendingCwd = '';
  app.pendingCwd = '';

  session.streaming = Boolean(message.state && message.state.isStreaming);
  showWorkspace(true);
  renderThread();
  renderHeader();
  renderComposerState();
  renderSessionList();

  if (!session.models) {
    send({ type: 'list_models', key });
  }
  send({ type: 'list_conversations' });

  if (app.pendingPrompt) {
    const text = app.pendingPrompt;
    app.pendingPrompt = '';
    el.input.value = text;
    submitPrompt();
  }
}

function onModels(message) {
  const session = app.byKey.get(message.key);
  if (!session) return;
  session.models = message.data || null;
  session.levels = message.thinkingLevels || null;
  if (message.key === app.currentKey) renderHeader();
}

function onProcessExit(message) {
  const session = app.byKey.get(message.key);
  app.byKey.delete(message.key);
  const detail = message.stderr ? '\n' + message.stderr.split('\n').slice(-4).join('\n') : '';
  toast('Pi 运行时退出了（退出码 ' + message.code + '），发一句话就会自动重连。' + detail, 'error');
  if (app.currentKey === message.key) {
    app.currentKey = null;
    showWorkspace(false);
    renderHeader();
  }
  if (session) renderSessionList();
}

/* ------------------------------------------------------------------ *
 * 事件流
 * ------------------------------------------------------------------ */

function liveItem(session) {
  if (!session.liveId) return null;
  return session.items.find((item) => item.id === session.liveId) || null;
}

function onEvent(key, event) {
  const session = app.byKey.get(key);
  if (!session || !event) return;

  switch (event.type) {
    case 'message_start': {
      const message = event.message || {};
      if (message.role === 'assistant') {
        const item = { id: 'live-' + session.seq++, kind: 'assistant', blocks: [], streaming: true };
        session.items.push(item);
        session.liveId = item.id;
        session.streaming = true;
        if (key === app.currentKey) {
          patchItem(item);
          scrollToBottom(true);
          renderComposerState();
        }
      }
      break;
    }

    case 'message_update':
      onAssistantUpdate(session, key, event.assistantMessageEvent);
      break;

    case 'message_end': {
      const message = event.message || {};
      if (message.role === 'assistant') {
        const item = liveItem(session);
        if (item) {
          item.blocks = blocksFromMessage(message);
          item.streaming = false;
          for (const block of item.blocks) {
            if (block.type === 'toolCall' && block.id) session.toolIndex.set(block.id, { item, block });
          }
          session.liveId = null;
          if (key === app.currentKey) schedule(item.id);
        }
      } else if (message.role === 'user') {
        const item = {
          id: 'i' + session.seq++,
          kind: 'user',
          text: contentText(message.content),
          images: contentImages(message.content),
        };
        session.items.push(item);
        if (key === app.currentKey) {
          patchItem(item);
          scrollToBottom(true);
        }
      }
      break;
    }

    case 'tool_execution_start': {
      const entry = session.toolIndex.get(event.toolCallId);
      if (entry) {
        entry.block.args = event.args || entry.block.args;
        entry.block.argsText = JSON.stringify(entry.block.args || {}, null, 2);
        entry.block.running = true;
        entry.block.done = false;
        entry.block.name = event.toolName || entry.block.name;
        if (key === app.currentKey) schedule(entry.item.id);
      }
      break;
    }

    case 'tool_execution_update': {
      const entry = session.toolIndex.get(event.toolCallId);
      if (entry && event.partialResult) {
        entry.block.output = contentText(event.partialResult.content);
        if (key === app.currentKey) schedule(entry.item.id);
      }
      break;
    }

    case 'tool_execution_end': {
      const entry = session.toolIndex.get(event.toolCallId);
      if (entry) {
        entry.block.running = false;
        entry.block.done = true;
        entry.block.isError = Boolean(event.isError);
        entry.block.durationMs = typeof event.durationMs === 'number' ? Math.round(event.durationMs) : null;
        if (event.result) {
          entry.block.output = contentText(event.result.content) || entry.block.output;
        }
        if (event.args) {
          entry.block.args = event.args;
          entry.block.argsText = JSON.stringify(event.args, null, 2);
        }
        if (key === app.currentKey) schedule(entry.item.id);
      }
      break;
    }

    case 'agent_settled': {
      session.streaming = false;
      const item = liveItem(session);
      if (item) {
        item.streaming = false;
        session.liveId = null;
      }
      // 收尾时把最后一条助手消息重画一次：思考块的标题与工具卡状态都随流式状态变。
      const last = [...session.items].reverse().find((candidate) => candidate.kind === 'assistant');
      if (last && key === app.currentKey) schedule(last.id);
      if (key === app.currentKey) renderComposerState();
      send({ type: 'refresh_stats', key });
      send({ type: 'list_conversations' });
      break;
    }

    case 'queue_update':
      if (key === app.currentKey) {
        const count = (event.steering || []).length + (event.followUp || []).length;
        el.hint.textContent = count ? '队列里还有 ' + count + ' 条' : '';
        if (!count) renderComposerState();
      }
      break;

    case 'thinking_level_changed':
      session.state.thinkingLevel = event.level;
      if (key === app.currentKey) renderHeader();
      break;

    case 'session_info_changed':
      session.state.sessionName = event.name || undefined;
      if (key === app.currentKey) renderHeader();
      break;

    case 'compaction_start':
      toast('开始压缩上下文');
      break;

    case 'compaction_end':
      toast('上下文已压缩');
      send({ type: 'refresh_stats', key });
      break;

    case 'extension_ui_request':
      onExtensionRequest(session, event);
      break;

    default:
      break;
  }
}

function onAssistantUpdate(session, key, update) {
  if (!update) return;
  const item = liveItem(session);
  if (!item) return;
  const index = typeof update.contentIndex === 'number' ? update.contentIndex : 0;
  session.streaming = true;

  switch (update.type) {
    case 'text_start':
      item.blocks[index] = { type: 'text', text: '' };
      break;
    case 'text_delta':
      if (!item.blocks[index] || item.blocks[index].type !== 'text') item.blocks[index] = { type: 'text', text: '' };
      item.blocks[index].text += update.delta || '';
      break;
    case 'text_end':
      item.blocks[index] = { type: 'text', text: update.content || '' };
      break;
    case 'thinking_start':
      item.blocks[index] = { type: 'thinking', text: '' };
      break;
    case 'thinking_delta':
      if (!item.blocks[index] || item.blocks[index].type !== 'thinking') item.blocks[index] = { type: 'thinking', text: '' };
      item.blocks[index].text += update.delta || '';
      break;
    case 'thinking_end':
      item.blocks[index] = { type: 'thinking', text: update.content || '' };
      break;
    case 'toolcall_start':
      item.blocks[index] = {
        type: 'toolCall',
        id: update.id,
        name: update.toolName,
        args: {},
        argsText: '',
        output: '',
        running: true,
        done: false,
        isError: false,
        durationMs: null,
      };
      session.toolIndex.set(update.id, { item, block: item.blocks[index] });
      break;
    case 'toolcall_delta':
      if (item.blocks[index] && item.blocks[index].type === 'toolCall') {
        item.blocks[index].argsText += update.delta || '';
      }
      break;
    case 'toolcall_end': {
      const block = item.blocks[index];
      if (block && block.type === 'toolCall') {
        const call = update.toolCall || {};
        block.args = call.arguments || {};
        block.argsText = JSON.stringify(block.args, null, 2);
        block.name = call.name || block.name;
        block.id = call.id || block.id;
        session.toolIndex.set(block.id, { item, block });
      }
      break;
    }
    default:
      break;
  }

  if (key === app.currentKey) {
    schedule(item.id);
    renderComposerState();
  }
}

/* ------------------------------------------------------------------ *
 * 扩展交互
 * ------------------------------------------------------------------ */

function onExtensionRequest(session, request) {
  const method = request.method;
  if (method === 'setStatus') {
    if (request.statusText == null) {
      app.extStatus.delete(request.statusKey);
    } else {
      app.extStatus.set(request.statusKey, stripAnsi(request.statusText));
    }
    renderExtStatus();
    return;
  }

  if (method === 'setWidget') {
    if (!request.widgetLines) {
      app.widgets.delete(request.widgetKey);
    } else {
      app.widgets.set(request.widgetKey, request.widgetLines);
    }
    renderExtStatus();
    return;
  }

  if (method === 'notify') {
    toast(stripAnsi(request.message), request.notifyType === 'error' ? 'error' : request.notifyType === 'warning' ? 'warning' : '');
    return;
  }

  if (method === 'setTitle') {
    document.title = request.title ? 'PiAgentStation · ' + stripAnsi(request.title) : 'PiAgentStation';
    return;
  }

  if (method === 'set_editor_text') {
    el.input.value = request.text || '';
    autoGrow();
    el.input.focus();
    return;
  }

  if (method === 'select' || method === 'confirm' || method === 'input' || method === 'editor') {
    app.openRequest = request;
    openDialog(session, request);
  }
}

function respond(session, id, payload) {
  send(Object.assign({ type: 'extension_response', key: session.key, id }, payload));
}

function openDialog(session, request) {
  closeModal();
  const card = document.createElement('div');
  card.className = 'modal';

  const title = document.createElement('h3');
  title.textContent = stripAnsi(request.title) || ({
    select: '请选择',
    confirm: '请确认',
    input: '请输入',
    editor: '编辑内容',
  }[request.method] || '需要你的输入');
  card.appendChild(title);

  if (request.message) {
    const body = document.createElement('div');
    body.className = 'modal-message';
    body.textContent = stripAnsi(request.message);
    card.appendChild(body);
  }

  const actions = document.createElement('div');
  actions.className = 'modal-actions';
  const finish = (payload) => {
    respond(session, request.id, payload);
    closeModal();
  };

  if (request.method === 'select') {
    const list = document.createElement('div');
    list.className = 'options';
    for (const option of request.options || []) {
      const button = document.createElement('button');
      button.className = 'option';
      button.textContent = option;
      button.addEventListener('click', () => finish({ value: option }));
      list.appendChild(button);
    }
    card.appendChild(list);
    const cancel = document.createElement('button');
    cancel.className = 'ghost-btn';
    cancel.textContent = '取消';
    cancel.addEventListener('click', () => finish({ cancelled: true }));
    actions.appendChild(cancel);
  } else if (request.method === 'confirm') {
    const no = document.createElement('button');
    no.className = 'ghost-btn';
    no.textContent = '否';
    no.addEventListener('click', () => finish({ confirmed: false }));
    const yes = document.createElement('button');
    yes.className = 'primary-btn';
    yes.textContent = '是';
    yes.addEventListener('click', () => finish({ confirmed: true }));
    const cancel = document.createElement('button');
    cancel.className = 'ghost-btn';
    cancel.textContent = '取消';
    cancel.addEventListener('click', () => finish({ cancelled: true }));
    actions.appendChild(cancel);
    actions.appendChild(no);
    actions.appendChild(yes);
    yes.focus();
  } else if (request.method === 'input') {
    const input = document.createElement('input');
    input.type = 'text';
    input.placeholder = request.placeholder || '';
    input.value = request.prefill || '';
    card.appendChild(input);
    const cancel = document.createElement('button');
    cancel.className = 'ghost-btn';
    cancel.textContent = '取消';
    cancel.addEventListener('click', () => finish({ cancelled: true }));
    const submit = document.createElement('button');
    submit.className = 'primary-btn';
    submit.textContent = '确定';
    submit.addEventListener('click', () => finish({ value: input.value }));
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') finish({ value: input.value });
    });
    actions.appendChild(cancel);
    actions.appendChild(submit);
    setTimeout(() => input.focus(), 0);
  } else {
    const area = document.createElement('textarea');
    area.value = request.prefill || '';
    card.appendChild(area);
    const cancel = document.createElement('button');
    cancel.className = 'ghost-btn';
    cancel.textContent = '取消';
    cancel.addEventListener('click', () => finish({ cancelled: true }));
    const submit = document.createElement('button');
    submit.className = 'primary-btn';
    submit.textContent = '保存';
    submit.addEventListener('click', () => finish({ value: area.value }));
    actions.appendChild(cancel);
    actions.appendChild(submit);
    setTimeout(() => area.focus(), 0);
  }

  card.appendChild(actions);
  el.modal.innerHTML = '';
  el.modal.appendChild(card);
  el.modal.hidden = false;
}

function closeModal() {
  app.openRequest = null;
  el.modal.hidden = true;
  el.modal.innerHTML = '';
}

el.modal.addEventListener('click', (event) => {
  if (event.target === el.modal) {
    // 点背景等同取消，避免工具一直挂着。
    const session = current();
    const request = app.openRequest;
    if (session && request) respond(session, request.id, { cancelled: true });
    closeModal();
  }
});

/* ------------------------------------------------------------------ *
 * 启动
 * ------------------------------------------------------------------ */

applyCustomCss('');
renderSessionList();
renderExtStatus();
renderComposerState();
showWorkspace(false);
renderHeader();
setConnectionState('正在连接…');
connect();
