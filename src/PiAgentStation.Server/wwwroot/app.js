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
  if (message.type === 'list_sessions' && !transport.queue.some((item) => item.type === 'list_sessions')) {
    transport.queue.push(message);
  }
  return false;
}

function setConnectionState(text, ok) {
  el.connStatus.textContent = text;
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
    send({ type: 'list_sessions' });
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
  filter: '',
  currentKey: null,
  byKey: new Map(),
  activePath: '',
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

function samePath(a, b) {
  // 两边的路径都由宿主给出，分隔符一致，只比较大小写差异。
  return Boolean(a) && String(a).toLowerCase() === String(b || '').toLowerCase();
}

function makeSession(key, state, messages, cwd, path) {
  const built = buildItems(messages || []);
  return {
    key,
    path: path || '',
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
  sessionRoot: document.getElementById('sessionRoot'),
  sessionTitle: document.getElementById('sessionTitle'),
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
  starters: document.getElementById('starters'),
  fileInput: document.getElementById('fileInput'),
};

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
      html += '<pre><code' + (chunk.lang || '') + '>' + escapeHtml(chunk.body.replace(/\n$/, '')) +
        '</code></pre>';
    } else {
      html += renderBlocks(chunk.body);
    }
  }
  return html;
}

function renderBlocks(body) {
  const lines = body.split('\n');
  const out = [];
  let list = null;
  let paragraph = [];

  const flushParagraph = () => {
    if (paragraph.length) {
      out.push('<p>' + renderInline(paragraph.join('\n')).replace(/\n/g, '<br />') + '</p>');
      paragraph = [];
    }
  };
  const flushList = () => {
    if (list) {
      out.push('<' + list.tag + '>' + list.items.map((item) => '<li>' + renderInline(item) + '</li>').join('') +
        '</' + list.tag + '>');
      list = null;
    }
  };

  for (const line of lines) {
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

    const bullet = /^[-*+]\s+(.*)$/.exec(trimmed);
    if (bullet) {
      flushParagraph();
      if (!list || list.tag !== 'ul') {
        flushList();
        list = { tag: 'ul', items: [] };
      }
      list.items.push(bullet[1]);
      continue;
    }

    const ordered = /^\d+[.)]\s+(.*)$/.exec(trimmed);
    if (ordered) {
      flushParagraph();
      if (!list || list.tag !== 'ol') {
        flushList();
        list = { tag: 'ol', items: [] };
      }
      list.items.push(ordered[1]);
      continue;
    }

    const tableRow = /^\|(.+)\|$/.exec(trimmed);
    if (tableRow) {
      flushParagraph();
      flushList();
      const cells = tableRow[1].split('|').map((cell) => cell.trim());
      if (cells.every((cell) => /^:?-{2,}:?$/.test(cell))) {
        continue;
      }
      out.push('<table><tr>' + cells.map((cell) => '<td>' + renderInline(cell) + '</td>').join('') + '</tr></table>');
      continue;
    }

    if (list) {
      list.items[list.items.length - 1] += '\n' + trimmed;
      continue;
    }

    paragraph.push(trimmed);
  }

  flushParagraph();
  flushList();
  return out.join('');
}

/* ------------------------------------------------------------------ *
 * 消息模型
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

function renderSessionList() {
  const filter = app.filter.trim().toLowerCase();
  const groups = new Map();

  for (const session of app.sessions) {
    const label = session.cwd || '未知目录';
    if (filter) {
      const haystack = [(session.name || ''), (session.title || ''), session.cwd].join(' ').toLowerCase();
      if (!haystack.includes(filter)) continue;
    }
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label).push(session);
  }

  el.sessionList.innerHTML = '';
  if (!groups.size) {
    const empty = document.createElement('div');
    empty.className = 'group-label';
    empty.textContent = app.sessions.length ? '没有匹配的会话' : '还没有会话记录';
    el.sessionList.appendChild(empty);
    return;
  }

  for (const [cwd, sessions] of groups) {
    const label = document.createElement('div');
    label.className = 'group-label';
    label.title = cwd;
    label.innerHTML = '<span class="folder">' + escapeHtml(baseName(cwd) || cwd) + '</span>';
    el.sessionList.appendChild(label);

    for (const session of sessions) {
      const button = document.createElement('button');
      button.className = 'session-item' + (samePath(session.path, app.activePath) ? ' active' : '');
      button.title = session.cwd;
      const name = session.name || session.title || '未命名会话';
      button.innerHTML =
        '<div class="row1"><span class="name">' + escapeHtml(name) + '</span>' +
        '<span class="time">' + relativeTime(session.updatedAt) + '</span></div>' +
        (session.name && session.title
          ? '<div class="snippet">' + escapeHtml(session.title) + '</div>'
          : '');
      button.addEventListener('click', () => openSession(session));
      el.sessionList.appendChild(button);
    }
  }
}

/* ------------------------------------------------------------------ *
 * 渲染：线程
 * ------------------------------------------------------------------ */

function itemNode(item, session) {
  const wrapper = document.createElement('div');
  wrapper.dataset.item = item.id;

  if (item.kind === 'user') {
    wrapper.className = 'msg user';
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

  wrapper.className = 'msg assistant';
  for (const block of item.blocks) {
    if (block.type === 'text') {
      if (!block.text) continue;
      const md = document.createElement('div');
      md.className = 'md';
      md.innerHTML = renderMarkdown(block.text);
      wrapper.appendChild(md);
    } else if (block.type === 'thinking') {
      if (!block.text) continue;
      const details = document.createElement('details');
      details.className = 'think';
      const summary = document.createElement('summary');
      summary.textContent = session.streaming && !item.blocks.some((b) => b.type === 'text' && b.text)
        ? '正在思考'
        : '思考';
      const body = document.createElement('div');
      body.className = 'think-body';
      body.textContent = block.text;
      details.appendChild(summary);
      details.appendChild(body);
      wrapper.appendChild(details);
    } else if (block.type === 'toolCall') {
      wrapper.appendChild(toolCard(block));
    } else if (block.type === 'image') {
      const img = document.createElement('img');
      img.src = block.url;
      img.style.maxWidth = '280px';
      img.style.borderRadius = '8px';
      wrapper.appendChild(img);
    }
  }

  return wrapper;
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
  app.nodeCache.clear();
  el.threadInner.innerHTML = '';
  if (!session) return;
  for (const item of session.items) {
    const node = itemNode(item, session);
    app.nodeCache.set(item.id, node);
    el.threadInner.appendChild(node);
  }
  scrollToBottom(true);
}

function patchItem(item) {
  const session = current();
  if (!session) return;
  const node = itemNode(item, session);
  const existing = app.nodeCache.get(item.id);
  if (existing && existing.isConnected) {
    existing.replaceWith(node);
  } else {
    el.threadInner.appendChild(node);
  }
  app.nodeCache.set(item.id, node);
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
    el.sessionTitle.textContent = '未选择会话';
    el.sessionCwd.textContent = '';
    el.modelSelect.hidden = true;
    el.thinkingSelect.hidden = true;
    el.compactBtn.hidden = true;
    el.ctxUsage.hidden = true;
    return;
  }

  const state = session.state || {};
  const firstUser = session.items.find((item) => item.kind === 'user');
  const title = state.sessionName || (firstUser && firstUser.text ? firstUser.text.split('\n')[0] : '') || '新会话';
  el.sessionTitle.textContent = title;
  el.sessionTitle.title = title;
  el.sessionCwd.textContent = session.cwd || '';

  const model = state.model;
  if (model) {
    const list = (session.models && session.models.models) || [];
    if (list.length > 1) {
      const wanted = model.provider + '/' + model.id;
      if (![...el.modelSelect.options].some((option) => option.value === wanted)) {
        el.modelSelect.innerHTML = '';
        for (const entry of list) {
          const option = document.createElement('option');
          option.value = entry.provider + '/' + entry.id;
          option.textContent = entry.name || entry.id;
          el.modelSelect.appendChild(option);
        }
      }
      el.modelSelect.value = wanted;
      el.modelSelect.hidden = false;
    } else {
      el.modelSelect.hidden = true;
    }
  }

  const levels = (session.levels && session.levels.levels) || [];
  if (levels.length > 1) {
    if (el.thinkingSelect.options.length !== levels.length) {
      el.thinkingSelect.innerHTML = '';
      for (const level of levels) {
        const option = document.createElement('option');
        option.value = level;
        option.textContent = level;
        el.thinkingSelect.appendChild(option);
      }
    }
    el.thinkingSelect.value = state.thinkingLevel || levels[0];
    el.thinkingSelect.hidden = false;
  } else {
    el.thinkingSelect.hidden = true;
  }

  el.compactBtn.hidden = false;
  renderContext();
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
  el.sendBtn.textContent = streaming ? '停止' : '发送';
  el.sendBtn.classList.toggle('stop', streaming);
  el.hint.textContent = streaming ? 'Enter 插入为引导（steer），Shift+Enter 换行' : 'Enter 发送，Shift+Enter 换行';
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
  el.composer.hidden = !hasSession;
  el.empty.hidden = hasSession;
}

/* ------------------------------------------------------------------ *
 * 会话流程
 * ------------------------------------------------------------------ */

function openSession(session) {
  if (!session || !session.path) return;
  const known = [...app.byKey.values()].find((candidate) => samePath(candidate.path, session.path));
  if (known) {
    app.currentKey = known.key;
    app.activePath = known.path || session.path;
    app.activeCwd = session.cwd || '';
    showWorkspace(true);
    renderThread();
    renderHeader();
    renderComposerState();
    renderSessionList();
    return;
  }

  setOverlay('正在打开会话…');
  send({ type: 'open_session', path: session.path });
  app.pendingCwd = session.cwd || '';
}

function newSession() {
  askCwd();
}

function askCwd() {
  closeModal();
  const card = document.createElement('div');
  card.className = 'modal';

  const title = document.createElement('h3');
  title.textContent = '在哪个目录开会话';
  card.appendChild(title);

  const note = document.createElement('div');
  note.className = 'modal-message';
  note.textContent = '填中心那台机器上的绝对路径。';
  card.appendChild(note);

  const input = document.createElement('input');
  input.type = 'text';
  input.placeholder = 'D:\work\project';
  input.value = app.lastCwd || app.activeCwd || '';
  card.appendChild(input);

  const recent = [...new Set(app.sessions.map((session) => session.cwd).filter(Boolean))].slice(0, 4);
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
  setOverlay('正在打开会话…');
  pendingCwd = cwd;
  app.lastCwd = cwd;
  if (!send({ type: 'new_session', cwd })) {
    setOverlay(null);
    toast('还没连上中心，等连接恢复再试。', 'warning');
  }
}

el.newSessionBtn.addEventListener('click', newSession);
el.emptyNewBtn.addEventListener('click', newSession);

el.sessionFilter.addEventListener('input', () => {
  app.filter = el.sessionFilter.value;
  renderSessionList();
});

el.modelSelect.addEventListener('change', () => {
  const session = current();
  if (!session) return;
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

el.input.addEventListener('input', autoGrow);

el.input.addEventListener('keydown', (event) => {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    submitPrompt();
  }
});

el.sendBtn.addEventListener('click', () => {
  const session = current();
  if (session && session.streaming) {
    send({ type: 'abort', key: session.key });
    toast('已请求停止');
    return;
  }
  submitPrompt();
});

function submitPrompt() {
  const session = current();
  if (!session) return;
  const text = el.input.value.trim();
  if (!text) return;

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
  for (const file of files) {
    const dataUrl = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(file);
    });
    const comma = dataUrl.indexOf(',');
    app.attachments.push({
      name: file.name,
      mimeType: file.type || 'image/png',
      data: dataUrl.slice(comma + 1),
      dataUrl,
    });
  }
  renderAttachments();
});

const starters = [
  '看一下这个目录的结构，说清各部分在做什么。',
  '跑一遍测试，把失败项和原因列出来。',
  '梳理现在最该修的三个问题，给出改动位置。',
];

for (const text of starters) {
  const button = document.createElement('button');
  button.className = 'starter';
  button.textContent = text;
  button.addEventListener('click', () => {
    el.input.value = text;
    autoGrow();
    el.input.focus();
  });
  el.starters.appendChild(button);
}

/* ------------------------------------------------------------------ *
 * 宿主消息分发
 * ------------------------------------------------------------------ */

let pendingCwd = '';

function dispatch(message) {
  switch (message.type) {
    case 'hello':
      if (message.sessionRoot) {
        app.sessionRoot = message.sessionRoot;
        el.sessionRoot.textContent = app.sessionRoot;
        el.sessionRoot.title = app.sessionRoot;
      }
      break;

    case 'session_list':
      app.sessions = message.items || [];
      app.sessionRoot = message.root || '';
      el.sessionRoot.textContent = app.sessionRoot;
      el.sessionRoot.title = app.sessionRoot;
      renderSessionList();
      break;

    case 'session_state':
      onSessionState(message);
      break;

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
  const session = makeSession(key, message.state, message.messages, cwd,
    message.state && message.state.sessionFile);
  session.stats = message.stats || null;
  if (previous) {
    session.models = previous.models;
    session.levels = previous.levels;
  }

  const file = message.state && message.state.sessionFile;
  app.byKey.set(key, session);
  app.currentKey = key;
  app.activePath = file || app.activePath;
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
  if (file) {
    // 新建会话后把最新列表补上，让刚产生的会话出现在侧栏。
    send({ type: 'list_sessions' });
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
  toast('Pi 运行时退出了（退出码 ' + message.code + '），点一个会话可以重新启动。' + detail, 'error');
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
      // 新会话的文件是首次消息后才落盘的，这一轮结束再刷一次列表才能看到它。
      send({ type: 'list_sessions' });
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

renderSessionList();
renderExtStatus();
renderComposerState();
showWorkspace(false);
setConnectionState('正在连接…');
connect();
