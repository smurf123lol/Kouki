'use strict';

const STATUS_RU = {
    new: 'новая',
    in_progress: 'в работе',
    done: 'выполнена',
    cancelled: 'отменена',
    needs_review: 'на разборе',
};

const OUTCOME_RU = {
    created: 'задача создана',
    executed: 'команда исполнена',
    needs_review: 'на разборе',
    failed: 'ошибка',
};

const TAB_IN_RU = { tasks: 'задачи', questions: 'вопросы', review: 'разбор', intake: 'приём', compare: 'проверка', actions: 'действия', telemetry: 'телеметрия' };

// Доска — пять колонок по статусам. «Отменено» свёрнута при каждой загрузке страницы;
// состояние сворачивания не запоминается (спека T78, §2.6).
const BOARD_COLUMNS = [
    { status: 'new', title: 'Inbox' },
    { status: 'in_progress', title: 'В работе' },
    { status: 'needs_review', title: 'Ожидание' },
    { status: 'done', title: 'Готово' },
    { status: 'cancelled', title: 'Отменено' },
];

// На доске только корневые задачи; подзадачи живут в чек-листе внутри карточки.
const BOARD_LIMIT = 500;
const AUTO_REFRESH_MS = 5000;
// Порог мобильной вёрстки; то же число задаёт одна media query в styles.css.
const MOBILE_BREAKPOINT = 768;

const PRIORITY_RU = { 1: 'низкий', 2: 'обычный', 3: 'высокий' };

const state = {
    token: localStorage.getItem('kouki.token') || '',
    tab: 'tasks',
    telemetryHours: 24,
    tag: '',
    boardTab: 'new',
    boardTasks: [],
    cancelledCollapsed: true,
    openSubtasks: new Set(),
    subtasks: new Map(),
    drafts: {},
    subtaskErrors: {},
    dragging: false,
    dragId: null,
    dragAfter: false,
    editing: false,
    taskId: null,
    detailId: null,
    detailSig: null,
    audioUrls: new Map(),
    timer: null,
};

const $ = (id) => document.getElementById(id);
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
}[ch]));

// Делегирование: event.target может быть текстовым узлом или заглушкой теста.
function closestOf(node, selector) {
    return node && typeof node.closest === 'function' ? node.closest(selector) : null;
}

// Мобильная вёрстка — до 768 px: одна колонка, вкладки, без перетаскивания.
function isBoardDesktop() {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return true;
    return window.matchMedia(`(min-width: ${MOBILE_BREAKPOINT}px)`).matches;
}

// Подтверждение спрашиваем только в браузере; в песочнице теста его нет.
function confirmAction(message) {
    if (typeof window === 'undefined' || typeof window.confirm !== 'function') return true;
    return window.confirm(message);
}

class AuthError extends Error {}

async function api(path, options = {}) {
    const headers = Object.assign({}, options.headers);
    if (state.token) headers.Authorization = 'Bearer ' + state.token;
    if (options.body) headers['Content-Type'] = 'application/json; charset=utf-8';

    const response = await fetch(path, Object.assign({}, options, { headers }));
    if (response.status === 401) throw new AuthError('неверный или отсутствующий токен');
    if (!response.ok) {
        let detail = '';
        try {
            const data = await response.json();
            detail = data.error || data.title || JSON.stringify(data);
        } catch { detail = await response.text(); }
        throw new Error(`${response.status}: ${detail}`);
    }
    return response.status === 204 ? null : response.json();
}

function showBanner(message) {
    const banner = $('banner');
    banner.textContent = message;
    banner.hidden = !message;
}

function setServerStatus(text, kind) {
    const node = $('server-status');
    node.textContent = text;
    node.className = 'brand-sub' + (kind ? ' is-' + kind : '');
}

function setCount(id, value) {
    const node = $(id);
    if (node) node.textContent = Number.isFinite(value) ? String(value) : '—';
}

function formatTime(iso) {
    if (!iso) return '—';
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    const today = new Date().toDateString() === date.toDateString();
    return today
        ? date.toLocaleTimeString('ru-RU', { hour12: false })
        : date.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

// Уверенность в журнале — доля 0…1; старые записи могут прийти без score.
function formatScore(score) {
    if (score === null || score === undefined || score === '') return '—';
    const value = Number(score);
    return Number.isFinite(value) ? Math.round(value * 100) + '%' : '—';
}

// Blob-URL аудио живут, пока открыта карточка: при перерисовке и закрытии их надо
// отзывать, иначе браузер держит в памяти каждую прослушанную запись.
function releaseAudioUrls() {
    state.audioUrls.forEach((url) => URL.revokeObjectURL(url));
    state.audioUrls.clear();
}

async function playPhraseAudio(id) {
    const status = document.querySelector(`[data-audio-status="${id}"]`);
    const player = document.querySelector(`[data-audio-player="${id}"]`);
    const setStatus = (text) => { if (status) status.textContent = text; };
    const play = () => {
        if (!player) return;
        player.hidden = false;
        if (typeof player.play !== 'function') return;
        const started = player.play();
        if (started && typeof started.catch === 'function') started.catch(() => {});
    };

    // Повторное нажатие не качает запись заново: URL уже лежит в state.audioUrls.
    const cached = state.audioUrls.get(id);
    if (cached) {
        setStatus('');
        if (player && player.src !== cached) player.src = cached;
        play();
        return;
    }

    setStatus('загружаю…');

    try {
        const response = await fetch('/api/audio/' + encodeURIComponent(id), {
            headers: state.token ? { Authorization: 'Bearer ' + state.token } : {},
        });

        if (response.status === 401) throw new AuthError('неверный или отсутствующий токен');
        if (!response.ok) {
            setStatus('Не удалось загрузить запись.');
            return;
        }

        const blob = await response.blob();
        const url = URL.createObjectURL(blob);
        state.audioUrls.set(id, url);
        setStatus('');
        if (player) player.src = url;
        play();
    } catch (error) {
        // 401 не маскируем под «запись не загрузилась»: это тот же отказ доступа,
        // что и у остальных запросов дашборда.
        if (error instanceof AuthError) {
            setServerStatus('нет доступа', 'down');
            showBanner('Нужен bearer-токен: введите его справа и нажмите «сохранить».');
            return;
        }
        setStatus('Не удалось загрузить запись.');
    }
}

/* ---------- сводка ---------- */

async function refreshTally() {
    const [tasks, questions, review, actions, telemetry] = await Promise.allSettled([
        api('/api/tasks?limit=200'),
        api('/api/questions?limit=100'),
        api('/api/classifications?unresolved=true&limit=100'),
        api('/api/actions?limit=100'),
        api('/api/telemetry?hours=24'),
    ]);

    setCount('count-tasks', tasks.status === 'fulfilled' ? tasks.value.length : NaN);
    setCount('count-questions', questions.status === 'fulfilled' ? questions.value.length : NaN);
    setCount('count-review', review.status === 'fulfilled' ? review.value.length : NaN);
    setCount('count-actions', actions.status === 'fulfilled' ? actions.value.length : NaN);
    setCount('count-telemetry', telemetry.status === 'fulfilled' && telemetry.value?.totals ? telemetry.value.totals.requests : NaN);

    const auth = [tasks, questions, review, actions, telemetry].some((result) => result.status === 'rejected' && result.reason instanceof AuthError);
    if (auth) throw new AuthError('нет токена');
}

/* ---------- точечное обновление списков ---------- */

// Автообновление не должно перерисовывать всё: иначе сбрасывается ввод в поле
// «своя метка», теряется фокус и прокрутка. Обновляем только изменившиеся узлы.
function isUserBusy() {
    const element = document.activeElement;
    if (!element) return false;
    if (element.isContentEditable) return true;
    return ['INPUT', 'TEXTAREA', 'SELECT'].includes(element.tagName);
}

function applyList(container, items, keyOf, signatureOf, render, emptyHtml) {
    if (!items.length) {
        if (container.dataset.empty !== '1') {
            container.innerHTML = emptyHtml;
            container.dataset.empty = '1';
        }
        return;
    }
    delete container.dataset.empty;

    const existing = new Map();
    container.querySelectorAll('[data-list-key]').forEach((node) => existing.set(node.dataset.listKey, node));

    const wanted = new Set();
    const ordered = [];

    items.forEach((item) => {
        const key = String(keyOf(item));
        const signature = String(signatureOf(item));
        wanted.add(key);

        let node = existing.get(key);
        if (node && node.dataset.listSig === signature) {
            ordered.push(node);
            return;
        }

        const holder = document.createElement('template');
        holder.innerHTML = render(item).trim();
        const fresh = holder.content.firstElementChild;
        fresh.dataset.listKey = key;
        fresh.dataset.listSig = signature;
        if (node) node.replaceWith(fresh);
        else container.append(fresh);
        ordered.push(fresh);
    });

    container.querySelectorAll('[data-list-key]').forEach((node) => {
        if (!wanted.has(node.dataset.listKey)) node.remove();
    });

    const current = Array.from(container.children);
    if (current.length !== ordered.length || current.some((node, index) => node !== ordered[index])) {
        container.replaceChildren(...ordered);
    }
}

/* ---------- задачи ---------- */

async function loadBoard() {
    const params = new URLSearchParams();
    params.set('topLevel', 'true');
    params.set('limit', String(BOARD_LIMIT));
    if (state.tag) params.set('tag', state.tag);

    const tasks = await api('/api/tasks?' + params.toString());
    state.boardTasks = tasks.slice().sort((a, b) => (Number(a.position) || 0) - (Number(b.position) || 0)
        || String(b.createdAt || '').localeCompare(String(a.createdAt || ''))
        || String(a.id).localeCompare(String(b.id)));

    renderBoard();

    const parts = [tasks.length ? `${tasks.length} карточек` : ''];
    if (state.tag) parts.push('тег #' + state.tag);
    $('tasks-meta').textContent = parts.filter(Boolean).join(' · ');
    setCount('count-tasks', tasks.length);
}

function boardColumnTasks(status) {
    return state.boardTasks.filter((task) => task.status === status);
}

function renderBoard() {
    BOARD_COLUMNS.forEach((column) => {
        const items = boardColumnTasks(column.status);
        $('board-count-' + column.status).textContent = String(items.length);

        applyList(
            $('board-body-' + column.status),
            items,
            (task) => task.id,
            boardSignature,
            renderBoardCard,
            '<div class="empty board-empty">Пока пусто.</div>');
    });
}

// Ключ перерисовки карточки: без статуса/позиции/подзадач карточка осталась бы
// на старом месте после move, а счётчик не обновился бы после подзадачи.
function boardSignature(task) {
    return [
        task.status,
        task.position ?? 0,
        task.parentId ?? '',
        task.subtasksCount ?? 0,
        task.subtasksDoneCount ?? 0,
        task.priority,
        task.title,
        (task.tags || []).join(','),
        task.updatedAt || task.createdAt,
        state.openSubtasks.has(task.id) ? 'open' : '',
        subtaskSignature(task.id),
        state.drafts[task.id] || '',
        state.subtaskErrors[task.id] || '',
    ].join('|');
}

function subtaskSignature(parentId) {
    if (!state.openSubtasks.has(parentId)) return '';
    return (state.subtasks.get(parentId) || [])
        .map((item) => item.id + ':' + item.status)
        .join(',');
}

function priorityBadge(priority) {
    const value = Number(priority) || 2;
    const title = PRIORITY_RU[value] || String(value);
    return `<span class="priority priority-${value}" title="приоритет: ${esc(title)}">${esc(title)}</span>`;
}

function subtaskRows(parentId) {
    const items = state.subtasks.get(parentId) || [];
    if (!items.length) return '<div class="subtask-empty">Подзадач нет</div>';

    return items.map((item) => {
        const closed = item.status === 'done' || item.status === 'cancelled';
        return `<label class="subtask${closed ? ' is-closed' : ''}">
            <input type="checkbox" data-subtask-toggle="${esc(item.id)}"${item.status === 'done' ? ' checked' : ''}>
            <span class="subtask-title">${esc(item.title)}</span>
        </label>`;
    }).join('');
}

function subtaskBox(task) {
    const count = Number(task.subtasksCount) || 0;
    const done = Number(task.subtasksDoneCount) || 0;
    const open = state.openSubtasks.has(task.id);
    const label = count > 0 ? `Подзадачи: ${count} · готово ${done}` : 'Подзадачи';
    const draft = state.drafts[task.id] || '';
    const error = state.subtaskErrors[task.id]
        ? `<span class="meta subtask-error" data-subtask-error="${esc(task.id)}">${esc(state.subtaskErrors[task.id])}</span>`
        : '';

    return `<div class="subtask-box">
        <button type="button" class="btn small" data-subtasks-open="${esc(task.id)}" aria-expanded="${open}">${esc(label)}</button>
        ${open ? `<div class="subtask-list">
            ${subtaskRows(task.id)}
            <div class="subtask-add">
                <input class="subtask-input" data-subtask-input="${esc(task.id)}" value="${esc(draft)}" placeholder="Добавить подзадачу">
                <button type="button" class="btn small" data-subtask-add="${esc(task.id)}">добавить</button>
            </div>
            ${error}
        </div>` : ''}
    </div>`;
}

function renderBoardCard(task) {
    // Статусы меняются тем же PATCH, что и в карточке задачи; на доске — прямо из карточки.
    const statusButtons = ['in_progress', 'done', 'cancelled']
        .filter((status) => status !== task.status)
        .map((status) => `<button type="button" class="btn small" data-board-status="${esc(status)}" data-board-status-id="${esc(task.id)}">${esc(STATUS_RU[status])}</button>`)
        .join('');

    return `<article class="board-card" data-board-card="${esc(task.id)}" data-task="${esc(task.id)}" draggable="true">
        <div class="board-card-head">
            <div class="board-card-title">${esc(task.title)}</div>
            ${priorityBadge(task.priority)}
        </div>
        ${tagChips(task.tags)}
        ${subtaskBox(task)}
        <div class="board-card-status">${statusButtons}</div>
        <div class="board-card-actions">
            <button type="button" class="btn small danger" data-delete-task="${esc(task.id)}" data-delete-mode="cascade">Удалить с подзадачами</button>
            <button type="button" class="btn small" data-delete-task="${esc(task.id)}" data-delete-mode="detach">Удалить, оставив подзадачи</button>
        </div>
    </article>`;
}

async function loadSubtasks(parentId) {
    const items = await api(`/api/tasks?parentId=${encodeURIComponent(parentId)}&limit=${BOARD_LIMIT}`);
    state.subtasks.set(parentId, items);
}

async function toggleSubtasks(parentId) {
    if (state.openSubtasks.has(parentId)) {
        state.openSubtasks.delete(parentId);
        renderBoard();
        return;
    }

    state.openSubtasks.add(parentId);
    try {
        await loadSubtasks(parentId);
    } catch (error) {
        if (error instanceof AuthError) throw error;
        state.subtasks.set(parentId, []);
    }
    renderBoard();
}

async function addSubtask(parentId) {
    const text = (state.drafts[parentId] || '').trim();
    if (!text) {
        state.subtaskErrors[parentId] = 'Введите текст подзадачи.';
        renderBoard();
        return;
    }

    delete state.subtaskErrors[parentId];
    try {
        await api('/api/tasks', {
            method: 'POST',
            body: JSON.stringify({ text, parentId }),
        });
        delete state.drafts[parentId];
        state.openSubtasks.add(parentId);
        // Поле могло исчезнуть вместе с фокусом: иначе тик автообновления замолчит навсегда.
        state.editing = false;
        await Promise.all([loadSubtasks(parentId), loadBoard()]);
        renderBoard();
    } catch (error) {
        if (error instanceof AuthError) throw error;
        state.subtaskErrors[parentId] = 'Не удалось создать подзадачу.';
        renderBoard();
    }
}

async function toggleSubtask(subtaskId) {
    const entry = Array.from(state.subtasks.entries())
        .find(([, items]) => items.some((item) => item.id === subtaskId));
    if (!entry) return;

    const child = entry[1].find((item) => item.id === subtaskId);
    const status = child.status === 'done' ? 'new' : 'done';
    try {
        await api(`/api/tasks/${subtaskId}`, {
            method: 'PATCH',
            body: JSON.stringify({ status }),
        });
        await loadSubtasks(entry[0]);
        await loadBoard();
    } catch (error) {
        if (error instanceof AuthError) throw error;
        showBanner('Не удалось изменить подзадачу.');
        await loadBoard();
    }
}

async function deleteTask(taskId, mode) {
    const task = state.boardTasks.find((item) => item.id === taskId);
    const count = task ? Number(task.subtasksCount) || 0 : 0;
    const message = mode === 'detach'
        ? `Удалить задачу? Подзадачи станут самостоятельными. Подзадач: ${count}.`
        : `Удалить задачу вместе со всеми подзадачами? Подзадач: ${count}.`;
    if (!confirmAction(message)) return;

    // Каскад задевает подзадачи, поэтому сервер требует явный confirm=true.
    const query = mode === 'detach' ? 'mode=detach' : 'mode=cascade&confirm=true';
    try {
        await api(`/api/tasks/${taskId}?${query}`, { method: 'DELETE' });
        state.openSubtasks.delete(taskId);
        state.subtasks.delete(taskId);
        delete state.drafts[taskId];
        delete state.subtaskErrors[taskId];
        await Promise.all([loadBoard(), refreshTally()]);
    } catch (error) {
        if (error instanceof AuthError) throw error;
        showBanner('Не удалось удалить задачу.');
    }
}

async function moveTask(taskId, status, position) {
    if (!isBoardDesktop()) return false;

    state.dragging = true;
    try {
        await api(`/api/tasks/${taskId}/move`, {
            method: 'POST',
            body: JSON.stringify({ status, position }),
        });
        // Ответ move приходит без тегов (ограничение П2), поэтому карточку из него
        // не рисуем, а перезапрашиваем доску — как это делает автообновление.
        await Promise.all([loadBoard(), refreshTally()]);
        return true;
    } catch (error) {
        if (error instanceof AuthError) throw error;
        showBanner('Не удалось переместить задачу.');
        await loadBoard();
        return false;
    } finally {
        state.dragging = false;
        state.dragId = null;
    }
}

function selectBoardTab(status) {
    state.boardTab = status;
    BOARD_COLUMNS.forEach((column) => {
        const tab = $('board-tab-' + column.status);
        if (tab) tab.classList.toggle('is-active', column.status === status);
        const node = $('board-column-' + column.status);
        if (node) node.classList.toggle('is-active', column.status === status);
    });
}

function setCancelledCollapsed(collapsed) {
    state.cancelledCollapsed = collapsed;
    const column = $('board-column-cancelled');
    if (column) column.classList.toggle('board-column-collapsed', collapsed);
    const button = $('board-toggle-cancelled');
    if (button) button.textContent = collapsed ? 'Показать отменённые' : 'Свернуть';
}

// Тег — метка, а не текст: чип кликабелен и ставит фильтр по тегу (повторный клик снимает).
function tagChips(tags) {
    if (!Array.isArray(tags) || !tags.length) return '';
    return '<div class="card-tags">' + tags
        .map((tag) => `<button type="button" class="tag" data-tag="${esc(tag)}">#${esc(tag)}</button>`)
        .join('') + '</div>';
}

/* ---------- разбор ---------- */

async function loadReview() {
    const items = await api('/api/classifications?unresolved=true&limit=100');
    const list = $('review-list');
    $('review-meta').textContent = items.length ? `${items.length} фраз` : '';
    setCount('count-review', items.length);

    applyList(
        list,
        items,
        (item) => item.id,
        (item) => [item.label, item.score, item.intent, item.error, item.hintedIntent].join('|'),
        (item) => `
        <div class="card" style="cursor:default">
            <div class="card-main">
                <div class="card-title">«${esc(item.inputText)}»</div>
                <div class="card-sub">
                    ${esc(item.source)} · ${esc(formatTime(item.createdAt))} ·
                    ${esc(item.intent)} / ${Number(item.score).toFixed(2)} · ${esc(item.model)}
                    ${item.error ? ' · <span style="color:var(--bad)">' + esc(item.error) + '</span>' : ''}
                </div>
                <div class="label-row">
                    <button class="btn small primary" data-label="add_task" data-id="${esc(item.id)}">В задачи</button>
                    <button class="btn small" data-label="complete_task" data-id="${esc(item.id)}">Завершить</button>
                    <button class="btn small" data-label="list_tasks" data-id="${esc(item.id)}">Список</button>
                    <input class="mono" data-custom="${esc(item.id)}" placeholder="своя метка" style="width:120px">
                    <button class="btn small" data-custom-save="${esc(item.id)}">пометить</button>
                </div>
            </div>
        </div>`,
        '<div class="empty">Всё разобрано — неуверенных фраз нет.</div>');
}

async function labelRecord(id, label) {
    if (!label) return;
    await api(`/api/classifications/${id}/label`, {
        method: 'POST',
        body: JSON.stringify({ label }),
    });
    await Promise.all([loadReview(), loadBoard(), refreshTally()]);
}

/* ---------- вопросы ---------- */

async function loadQuestions() {
    const list = $('questions-list');
    const questions = await api('/api/questions?limit=100');
    $('questions-meta').textContent = questions.length ? `${questions.length} вопросов` : '';
    setCount('count-questions', questions.length);

    applyList(
        list,
        questions,
        (q) => q.id,
        (q) => [q.taskId || '', q.question, q.answer, q.model || ''].join('|'),
        (q) => `
        <article class="card question-card" data-question-id="${esc(q.id)}" style="padding: 16px; margin-bottom: 12px; background: var(--bg-card); border: 1px solid var(--border); border-radius: 8px;">
            <div style="display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; margin-bottom: 8px;">
                <h3 style="margin: 0; font-size: 15px; font-weight: 600;">${esc(q.question)}</h3>
                <div style="display: flex; gap: 8px; align-items: center; flex-shrink: 0;">
                    ${q.taskId
                        ? `<a href="#" class="btn small mono" data-task="${esc(q.taskId)}">Задача #${esc(String(q.taskId).slice(0, 8))}</a>`
                        : `<button class="btn small primary" type="button" data-create-task="${esc(q.id)}">+ В задачу</button>`}
                </div>
            </div>
            <div class="question-answer" style="margin: 8px 0; padding: 10px 14px; background: var(--bg); border-left: 3px solid var(--accent); border-radius: 4px; font-size: 14px; line-height: 1.5; color: var(--text);">
                ${esc(q.answer || '—')}
            </div>
            <div class="meta" style="font-size: 12px; color: var(--muted); display: flex; gap: 12px; flex-wrap: wrap;">
                <span>${esc(formatTime(q.createdAt))}</span>
                <span>источник: <b>${esc(q.source)}</b></span>
                ${q.model ? `<span>модель: <b>${esc(q.model)}</b></span>` : ''}
                ${q.durationMs != null ? `<span>${q.durationMs} мс</span>` : ''}
            </div>
        </article>`,
        '<div class="empty">Вопросов ещё не задавали.</div>');
}

/* ---------- действия ---------- */

async function loadActions(query = '') {
    const url = query ? `/api/actions?limit=100&q=${encodeURIComponent(query)}` : '/api/actions?limit=100';
    const actions = await api(url);
    const body = $('actions-table').querySelector('tbody');
    $('actions-meta').textContent = actions.length ? `${actions.length} записей` : (query ? 'Ничего не найдено' : '');
    if (!query) setCount('count-actions', actions.length);

    applyList(
        body,
        actions,
        (action) => action.id,
        (action) => [action.outcome, action.durationMs, action.command, action.error, action.taskId].join('|'),
        (action) => `
        <tr>
            <td class="mono">${esc(formatTime(action.createdAt))}</td>
            <td>${esc(action.source)}</td>
            <td class="outcome-${esc(action.outcome)}">${esc(OUTCOME_RU[action.outcome] || action.outcome)}</td>
            <td>${esc(action.intent || '')}</td>
            <td class="mono">${esc(action.command || '')}</td>
            <td class="mono">${action.durationMs ?? ''}</td>
            <td>${action.taskId
                ? `<a href="#" class="mono" data-task="${esc(action.taskId)}">${esc(String(action.taskId).slice(0, 8))}</a>`
                : '<span class="muted">—</span>'}</td>
        </tr>`,
        `<tr><td colspan="7" class="empty">${query ? 'Ничего не найдено по запросу.' : 'Действий ещё не было.'}</td></tr>`);
}

/* ---------- телеметрия ---------- */

function formatBytes(bytes) {
    if (!bytes || bytes === 0) return '0 Б';
    if (bytes < 1024) return bytes + ' Б';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' КБ';
    return (bytes / (1024 * 1024)).toFixed(1) + ' МБ';
}

async function loadTelemetry() {
    const hours = state.telemetryHours || 24;
    const telemetry = await api(`/api/telemetry?hours=${hours}`);

    setCount('count-telemetry', telemetry.totals.requests);
    $('telemetry-meta').textContent = `${telemetry.totals.requests} запросов за ${hours} ч · с ${formatTime(telemetry.since)}`;

    $('telemetry-requests').textContent = telemetry.totals.requests;
    $('telemetry-errors').textContent = telemetry.totals.errors;
    $('telemetry-avg-duration').textContent = `${telemetry.totals.avgDurationMs} мс`;
    $('telemetry-audio-bytes').textContent = formatBytes(telemetry.totals.audioBytes);

    const epTable = $('telemetry-endpoints-table');
    if (epTable) {
        const epBody = epTable.querySelector('tbody');
        if (epBody) {
            epBody.innerHTML = (telemetry.byEndpoint || []).map((ep) => `
                <tr>
                    <td class="mono">${esc(ep.endpoint)}</td>
                    <td class="mono">${ep.requests}</td>
                    <td class="mono ${ep.errors > 0 ? 'err' : ''}">${ep.errors}</td>
                    <td class="mono">${ep.avgDurationMs}</td>
                    <td class="mono">${formatBytes(ep.audioBytes)}</td>
                </tr>`).join('') || '<tr><td colspan="5" class="empty">Запросов за это окно не было.</td></tr>';
        }
    }

    const srcTable = $('telemetry-sources-table');
    if (srcTable) {
        const srcBody = srcTable.querySelector('tbody');
        if (srcBody) {
            srcBody.innerHTML = (telemetry.bySource || []).map((src) => `
                <tr>
                    <td class="mono">${esc(src.source)}</td>
                    <td class="mono">${src.requests}</td>
                    <td class="mono ${src.errors > 0 ? 'err' : ''}">${src.errors}</td>
                </tr>`).join('') || '<tr><td colspan="3" class="empty">Источников за это окно не было.</td></tr>';
        }
    }
}

/* ---------- карточка задачи ---------- */

async function openTask(id, options = {}) {
    const force = options.force === true;
    const sameTask = state.detailId === id;
    state.taskId = id;
    $('detail').hidden = false;

    if (force || !sameTask) {
        state.detailId = id;
        $('detail-title').textContent = 'Загрузка…';
        $('detail-body').innerHTML = '';
        state.detailSig = null;
        releaseAudioUrls();
    }

    const [task, actions, classifications] = await Promise.all([
        api(`/api/tasks/${id}`),
        api(`/api/actions?taskId=${id}&limit=100`),
        api(`/api/classifications?taskId=${id}&limit=50`),
    ]);

    // Автообновление не должно перерисовывать карточку, если ничего не поменялось:
    // иначе прокрутка таймлайна прыгает к началу каждые пять секунд, а играющее
    // аудио обрывается. Фразы входят в ключ: без них новые записи не появятся.
    const signature = [
        task.id, task.updatedAt, task.status, task.title,
        actions.length, actions[0]?.id ?? '',
        classifications.length,
        classifications[0]?.id ?? '',
        classifications[classifications.length - 1]?.id ?? '',
    ].join('|');
    if (!force && state.detailSig === signature) return;
    state.detailSig = signature;
    releaseAudioUrls();

    $('detail-title').textContent = task.title;

    const buttons = ['in_progress', 'done', 'cancelled']
        .filter((status) => status !== task.status)
        .map((status) => `<button class="btn small" data-set-status="${esc(status)}">${esc(STATUS_RU[status])}</button>`)
        .join('');

    const timeline = actions.length
        ? actions.map((action) => `
            <div class="entry">
                <div class="entry-top">
                    <span class="outcome-${esc(action.outcome)}">${esc(OUTCOME_RU[action.outcome] || action.outcome)}</span>
                    <span class="muted mono">${esc(formatTime(action.createdAt))} · ${action.durationMs ?? '?'} мс</span>
                </div>
                <div class="entry-meta">
                    ${esc(action.source)}${action.command ? ' · команда ' + esc(action.command) : ''}
                    ${action.error ? ' · <span style="color:var(--bad)">' + esc(action.error) + '</span>' : ''}
                </div>
            </div>`).join('')
        : '<div class="empty">Действий по этой задаче нет.</div>';

    const phrases = classifications.length
        ? classifications.map((item) => `
            <div class="entry phrase" data-phrase="${esc(item.id)}">
                <div class="entry-top">
                    <span class="phrase-text">«${esc(item.inputText)}»</span>
                    <span class="muted mono">${esc(formatTime(item.createdAt))}</span>
                </div>
                <div class="entry-meta">
                    ${esc(item.source)} · <span class="phrase-intent mono">${esc(item.intent)}</span> · ${esc(formatScore(item.score))}
                    ${item.confident === false ? '<span class="phrase-flag">неуверенно</span>' : ''}
                    ${item.error ? `<span class="phrase-flag is-bad">${esc(item.error)}</span>` : ''}
                </div>
                <div class="entry-meta">модель: <span class="mono">${esc(item.model)}</span></div>
                ${item.hasAudio === true ? `
                <div class="phrase-audio">
                    <button type="button" class="btn small" data-audio-play="${esc(item.id)}">▶ прослушать</button>
                    <audio controls preload="none" data-audio-player="${esc(item.id)}" hidden></audio>
                    <span class="muted" data-audio-status="${esc(item.id)}"></span>
                </div>` : ''}
            </div>`).join('')
        : '<div class="empty">Связанных фраз нет.</div>';

    const meta = task.meta
        ? `<pre class="result" style="max-height:160px">${esc(JSON.stringify(task.meta, null, 2))}</pre>`
        : '<span class="muted">нет</span>';

    $('detail-body').innerHTML = `
        <dl class="kv">
            <dt>id</dt><dd class="mono">${esc(task.id)}</dd>
            <dt>статус</dt><dd><span class="status status-${esc(task.status)}">${esc(STATUS_RU[task.status] || task.status)}</span></dd>
            <dt>источник</dt><dd>${esc(task.source)}</dd>
            <dt>приоритет</dt><dd>${esc(task.priority)}</dd>
            <dt>создана</dt><dd>${esc(formatTime(task.createdAt))}</dd>
            <dt>изменена</dt><dd>${esc(formatTime(task.updatedAt))}</dd>
            <dt>фраза</dt><dd>«${esc(task.rawText)}»</dd>
            <dt>теги</dt><dd>${tagChips(task.tags) || '<span class="muted">—</span>'}</dd>
        </dl>
        <div>
            <div class="section-title">Действия</div>
            <div class="timeline">${timeline}</div>
        </div>
        <div>
            <div class="section-title">История распознавания</div>
            <div class="timeline phrases">${phrases}</div>
        </div>
        <div>
            <div class="section-title">Разбор (meta)</div>
            ${meta}
        </div>
        <div class="row-actions">${buttons}</div>`;
}

async function setTaskStatus(status) {
    if (!state.taskId) return;
    await api(`/api/tasks/${state.taskId}`, {
        method: 'PATCH',
        body: JSON.stringify({ status }),
    });
    await Promise.all([loadBoard(), openTask(state.taskId, { force: true }), refreshTally()]);
}

async function setBoardTaskStatus(taskId, status) {
    try {
        await api(`/api/tasks/${taskId}`, {
            method: 'PATCH',
            body: JSON.stringify({ status }),
        });
        await Promise.all([loadBoard(), refreshTally()]);
    } catch (error) {
        if (error instanceof AuthError) throw error;
        showBanner('Не удалось изменить статус задачи.');
        await loadBoard();
    }
}

/* ---------- приём ---------- */

async function submitIntake(event) {
    event.preventDefault();
    const text = $('intake-text').value.trim();
    if (!text) return;

    const result = $('intake-result');
    result.hidden = false;
    result.textContent = 'Отправляю…';

    try {
        const response = await api('/api/intake', {
            method: 'POST',
            body: JSON.stringify({ text, source: $('intake-source').value }),
        });
        result.textContent = JSON.stringify(response, null, 2);
        $('intake-text').value = '';
        await Promise.all([refreshTally(), loadActions()]);
    } catch (error) {
        result.textContent = 'Ошибка: ' + error.message;
    }
}

/* ---------- проверка: сравнение классификаторов и распознавание файла ---------- */

const selectedClassifiers = new Set();

async function loadClassifiers() {
    const list = $('classifier-list');
    if (list.dataset.loaded === 'true') return;

    const response = await api('/api/classifiers');
    const names = response.classifiers || [];

    list.innerHTML = names.map((name) => {
        /* «+journal» пишет в журнал при каждом прогоне — по умолчанию выключен. */
        const checked = !name.includes('+journal');
        if (checked) selectedClassifiers.add(name);
        return `<button type="button" class="chip${checked ? ' is-active' : ''}" data-classifier="${esc(name)}">${esc(name)}</button>`;
    }).join('');

    list.dataset.loaded = 'true';
}

function renderCompare(response) {
    const body = $('compare-body');
    const results = response.results || [];

    if (results.length === 0) {
        body.innerHTML = '<tr><td colspan="6" class="meta">нет выбранных классификаторов</td></tr>';
        return;
    }

    body.innerHTML = results.map((item) => `<tr>
        <td>${esc(item.classifier)}</td>
        <td>${esc(item.intent)}</td>
        <td>${(item.score * 100).toFixed(0)} %</td>
        <td>${item.confident ? 'да' : 'нет'} (${(response.threshold * 100).toFixed(0)} %)</td>
        <td>${esc(item.model)}</td>
        <td class="err">${esc(item.error || '')}</td>
    </tr>`).join('');
}

async function compareText(event) {
    if (event) event.preventDefault();
    const text = $('compare-text').value.trim();
    const result = $('compare-result');
    if (!text) return;

    try {
        const body = { text, classifiers: Array.from(selectedClassifiers) };
        const response = await api('/api/classify', { method: 'POST', body: JSON.stringify(body) });
        renderCompare(response);
        result.hidden = true;
    } catch (error) {
        result.hidden = false;
        result.textContent = 'Ошибка: ' + error.message;
    }
}

async function transcribeFile() {
    const input = $('transcribe-file');
    const meta = $('transcribe-meta');
    const file = input.files && input.files[0];

    if (!file) {
        meta.textContent = 'выберите WAV-файл';
        return;
    }

    meta.textContent = 'распознаю…';

    try {
        const buffer = await file.arrayBuffer();
        const response = await fetch('/api/transcribe', {
            method: 'POST',
            headers: {
                'Content-Type': 'audio/wav',
                ...(state.token ? { Authorization: `Bearer ${state.token}` } : {}),
            },
            body: buffer,
        });

        if (response.status === 401) throw new AuthError('401');

        const payload = await response.json().catch(() => ({}));

        if (!response.ok) {
            meta.textContent = payload.error || `код ${response.status}`;
            return;
        }

        meta.textContent = `услышано за ${payload.durationSec.toFixed(2)} с`;
        $('compare-text').value = payload.transcript || '';
        await compareText(null);
    } catch (error) {
        meta.textContent = error instanceof AuthError ? 'нужен токен' : 'ошибка: ' + error.message;
    }
}

/* ---------- переключение вкладок ---------- */

async function switchTab(tab) {
    state.tab = tab;
    document.querySelectorAll('.tile').forEach((node) => {
        node.classList.toggle('is-active', node.dataset.tab === tab);
    });
    $('panel-tasks').hidden = tab !== 'tasks';
    $('panel-questions').hidden = tab !== 'questions';
    $('panel-review').hidden = tab !== 'review';
    $('panel-intake').hidden = tab !== 'intake';
    $('panel-compare').hidden = tab !== 'compare';
    $('panel-actions').hidden = tab !== 'actions';
    $('panel-telemetry').hidden = tab !== 'telemetry';

    await reload();
}

async function reload(options = {}) {
    const force = options.force === true;
    try {
        showBanner('');
        await refreshTally();

        // Пока в поле ввода печатают или карточку тащат, списки не трогаем: обновим
        // табло и статус, а разметку — когда фокус уйдёт (или нажали «обновить»).
        if (!force && (isUserBusy() || state.dragging || state.editing)) return;

        if (state.tab === 'tasks') await loadBoard();
        else if (state.tab === 'questions') await loadQuestions();
        else if (state.tab === 'review') await loadReview();
        else if (state.tab === 'compare') await loadClassifiers();
        else if (state.tab === 'actions') await loadActions($('actions-search')?.value.trim() || '');
        else if (state.tab === 'telemetry') await loadTelemetry();
        if (state.taskId) await openTask(state.taskId, { force });
    } catch (error) {
        if (error instanceof AuthError) {
            showBanner('Нужен bearer-токен: введите его справа и нажмите «сохранить».');
            setServerStatus('нет доступа', 'down');
        } else {
            showBanner('Ошибка: ' + error.message);
            setServerStatus('ошибка', 'down');
        }
    }
}

async function ping() {
    try {
        const health = await api('/api/health');
        setServerStatus('на связи · ' + formatTime(health.utc), 'up');
        showBanner('');
    } catch (error) {
        setServerStatus(error instanceof AuthError ? 'нет доступа' : 'нет связи', 'down');
        if (error instanceof AuthError) showBanner('Нужен bearer-токен: введите его справа и нажмите «сохранить».');
    }
}

/* ---------- события ---------- */

document.addEventListener('click', async (event) => {
    // Чип тега живёт внутри карточки задачи: сначала он, иначе клик по тегу откроет задачу.
    const tagChip = closestOf(event.target, '[data-tag]');
    if (tagChip) {
        event.preventDefault();
        event.stopPropagation();
        state.tag = state.tag === tagChip.dataset.tag ? '' : tagChip.dataset.tag;
        await loadBoard();
        return;
    }

    // Поле «Добавить подзадачу» не открывает карточку задачи.
    if (closestOf(event.target, '[data-subtask-input]')) return;

    const subtasksOpen = closestOf(event.target, '[data-subtasks-open]');
    if (subtasksOpen) {
        await toggleSubtasks(subtasksOpen.dataset.subtasksOpen);
        return;
    }

    const subtaskToggle = closestOf(event.target, '[data-subtask-toggle]');
    if (subtaskToggle) {
        await toggleSubtask(subtaskToggle.dataset.subtaskToggle);
        return;
    }

    const subtaskAdd = closestOf(event.target, '[data-subtask-add]');
    if (subtaskAdd) {
        await addSubtask(subtaskAdd.dataset.subtaskAdd);
        return;
    }

    const deleteButton = closestOf(event.target, '[data-delete-task]');
    if (deleteButton) {
        await deleteTask(deleteButton.dataset.deleteTask, deleteButton.dataset.deleteMode);
        return;
    }

    // Кнопки статусов на карточке доски: тот же PATCH, что и в панели задачи.
    const boardStatus = closestOf(event.target, '[data-board-status]');
    if (boardStatus) {
        await setBoardTaskStatus(boardStatus.dataset.boardStatusId, boardStatus.dataset.boardStatus);
        return;
    }

    // Аудио в карточке качается только по нажатию: preload="none" и src не задан.
    const audioButton = closestOf(event.target, '[data-audio-play]');
    if (audioButton) {
        event.preventDefault();
        await playPhraseAudio(audioButton.dataset.audioPlay);
        return;
    }

    const target = closestOf(event.target, '[data-task]');
    if (target) {
        event.preventDefault();
        await openTask(target.dataset.task, { force: true });
        return;
    }

    const labelButton = closestOf(event.target, '[data-label]');
    if (labelButton) {
        await labelRecord(labelButton.dataset.id, labelButton.dataset.label);
        return;
    }

    const customSave = closestOf(event.target, '[data-custom-save]');
    if (customSave) {
        const id = customSave.dataset.customSave;
        const input = document.querySelector(`[data-custom="${id}"]`);
        await labelRecord(id, input.value.trim());
        return;
    }

    const statusButton = closestOf(event.target, '[data-set-status]');
    if (statusButton) {
        await setTaskStatus(statusButton.dataset.setStatus);
        return;
    }

    const telemetryHoursChip = closestOf(event.target, '[data-telemetry-hours]');
    if (telemetryHoursChip) {
        event.preventDefault();
        const hours = parseInt(telemetryHoursChip.dataset.telemetryHours, 10);
        if (hours > 0) {
            state.telemetryHours = hours;
            document.querySelectorAll('[data-telemetry-hours]').forEach((node) => {
                node.classList.toggle('is-active', node === telemetryHoursChip);
            });
            await loadTelemetry();
        }
    }
});

// Вкладки колонок нужны только на мобильной ширине: на десктопе видны все пять.
$('board-tabs').addEventListener('click', (event) => {
    const tab = closestOf(event.target, '[data-board-tab]');
    if (!tab) return;
    selectBoardTab(tab.dataset.boardTab);
});

// «Отменено» сворачивается кликом по шапке; состояние не запоминается между загрузками.
$('board-head-cancelled').addEventListener('click', () => {
    setCancelledCollapsed(!state.cancelledCollapsed);
});

// Черновик подзадачи живёт в состоянии, пока поле в фокусе: автообновление его не сбрасывает.
document.addEventListener('input', (event) => {
    const input = closestOf(event.target, '[data-subtask-input]');
    if (!input) return;
    const id = input.dataset.subtaskInput;
    state.drafts[id] = input.value;
    state.editing = true;
    if (state.subtaskErrors[id]) {
        delete state.subtaskErrors[id];
        renderBoard();
    }
});

document.addEventListener('focusout', (event) => {
    if (closestOf(event.target, '[data-subtask-input]')) state.editing = false;
});

// Перетаскивание: только десктоп. Позиция — индекс в целевой колонке при отрисовке.
document.addEventListener('dragstart', (event) => {
    if (!isBoardDesktop()) {
        event.preventDefault();
        return;
    }
    const card = closestOf(event.target, '[data-board-card]');
    if (!card) return;

    state.dragging = true;
    state.dragId = card.dataset.boardCard;
    if (event.dataTransfer) {
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', state.dragId);
    }
});

document.addEventListener('dragover', (event) => {
    if (!isBoardDesktop() || !state.dragId) return;
    const column = closestOf(event.target, '[data-column]');
    if (!column) return;
    event.preventDefault();

    const card = closestOf(event.target, '[data-board-card]');
    state.dragAfter = false;
    if (card && typeof card.getBoundingClientRect === 'function' && typeof event.clientY === 'number') {
        const rect = card.getBoundingClientRect();
        state.dragAfter = rect.height > 0 && event.clientY - rect.top > rect.height / 2;
    }
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
});

document.addEventListener('drop', async (event) => {
    if (!isBoardDesktop() || !state.dragId) return;
    const column = closestOf(event.target, '[data-column]');
    if (!column) return;
    event.preventDefault();

    const status = column.dataset.column;
    const draggedId = state.dragId;
    const card = closestOf(event.target, '[data-board-card]');
    const columnTasks = boardColumnTasks(status).filter((task) => task.id !== draggedId);

    let position = columnTasks.length;
    if (card) {
        const at = columnTasks.findIndex((task) => task.id === card.dataset.boardCard);
        if (at >= 0) position = at + (state.dragAfter ? 1 : 0);
    }

    state.dragAfter = false;
    await moveTask(draggedId, status, position);
});

document.addEventListener('dragend', () => {
    state.dragging = false;
    state.dragId = null;
    state.dragAfter = false;
});

document.querySelectorAll('.tile[data-tab]').forEach((node) => {
    node.addEventListener('click', () => switchTab(node.dataset.tab));
});

$('detail-close').addEventListener('click', () => {
    state.taskId = null;
    state.detailId = null;
    state.detailSig = null;
    releaseAudioUrls();
    $('detail').hidden = true;
});

$('intake-form').addEventListener('submit', submitIntake);
$('compare-form').addEventListener('submit', compareText);
$('transcribe-btn').addEventListener('click', transcribeFile);

$('classifier-list').addEventListener('click', (event) => {
    const chip = event.target.closest('[data-classifier]');
    if (!chip) return;

    const name = chip.dataset.classifier;
    if (selectedClassifiers.has(name)) {
        selectedClassifiers.delete(name);
        chip.classList.remove('is-active');
    } else {
        selectedClassifiers.add(name);
        chip.classList.add('is-active');
    }
});

$('questions-list')?.addEventListener('click', async (e) => {
    const btn = e.target.closest('[data-create-task]');
    if (!btn) return;
    const qid = btn.dataset.createTask;
    btn.disabled = true;
    try {
        const created = await api(`/api/questions/${qid}/task`, { method: 'POST' });
        showBanner(`Задача создана: ${created.title || ''}`, 'ok');
        await Promise.all([loadQuestions(), loadBoard(), refreshTally()]);
    } catch (err) {
        showBanner(`Ошибка: ${err.message}`);
    } finally {
        btn.disabled = false;
    }
});

let actionsSearchTimer = null;
$('actions-search')?.addEventListener('input', (e) => {
    clearTimeout(actionsSearchTimer);
    actionsSearchTimer = setTimeout(() => {
        loadActions(e.target.value.trim());
    }, 300);
});

$('save-token').addEventListener('click', async () => {
    state.token = $('token').value.trim();
    localStorage.setItem('kouki.token', state.token);
    await ping();
    await reload();
});

$('refresh').addEventListener('click', () => reload({ force: true }));

document.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && state.tab === 'intake') {
        $('intake-form').requestSubmit();
    }
});

/* ---------- старт ---------- */

$('token').value = state.token;
setCancelledCollapsed(true);
selectBoardTab(state.boardTab);
switchTab('tasks').then(ping);
state.timer = setInterval(() => {
    if (document.hidden) return;
    if (state.tab === 'intake') return;
    // Тик не должен ломать перетаскивание или недописанную подзадачу.
    if (state.dragging || state.editing) return;
    reload();
}, AUTO_REFRESH_MS);

console.info('Kouki dashboard: вкладка', TAB_IN_RU[state.tab]);
