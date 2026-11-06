// Перетаскивание задач мышью между блоками (беклог / следующий спринт / текущий спринт).
// Своя реализация на событиях мыши: HTML5 drag-and-drop в Blazor Server капризничает (нужен dragover без обработчика, ломается при перерисовке).
// Тянуть можно строку задачи за любое место (кроме кнопок, полей и галочки). Если задача отмечена галочкой, едет вся отмеченная группа.
window.cccDnD = (function () {
    let ref = null, st = null, ghost = null, hot = null, scrollTimer = null, lastY = 0;

    const keyOf = row => row.querySelector('.key').textContent.trim();
    const keysFor = row => {
        const cb = row.querySelector('.sel');
        if (cb && cb.checked) {
            return [...document.querySelectorAll('.item-row')]
                .filter(r => r.querySelector('.sel') && r.querySelector('.sel').checked)
                .map(keyOf);
        }
        return [keyOf(row)];
    };
    const zoneAt = (x, y) => {
        const el = document.elementFromPoint(x, y);
        return el && el.closest ? el.closest('.dropzone') : null;
    };

    function begin(e) {
        st.active = true;
        st.keys = keysFor(st.row);
        ghost = document.createElement('div');
        ghost.className = 'drag-ghost';
        ghost.textContent = st.keys.length > 1 ? `${st.keys.length} задач` : st.keys[0];
        document.body.appendChild(ghost);
        document.body.classList.add('dragging');
        if (window.getSelection) window.getSelection().removeAllRanges();
        scrollTimer = setInterval(() => {
            const h = window.innerHeight;
            if (lastY < 70) window.scrollBy(0, -18);
            else if (lastY > h - 70) window.scrollBy(0, 18);
        }, 30);
    }

    function finish(e, drop) {
        if (!st) return;
        const s = st;
        st = null;
        if (!s.active) return;
        clearInterval(scrollTimer);
        document.body.classList.remove('dragging');
        if (ghost) { ghost.remove(); ghost = null; }
        const z = hot || zoneAt(e.clientX, e.clientY);
        if (hot) { hot.classList.remove('drop-hover'); hot = null; }
        // после перетаскивания не считаем это кликом по строке (иначе раскроется карточка)
        const swallow = ev => { ev.stopPropagation(); ev.preventDefault(); };
        document.addEventListener('click', swallow, { capture: true, once: true });
        setTimeout(() => document.removeEventListener('click', swallow, true), 100);
        if (drop && z && ref) ref.invokeMethodAsync('DropKeys', z.dataset.zone, s.keys);
    }

    document.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        const t = e.target;
        if (!t.closest) return;
        const row = t.closest('.item-row');
        if (!row || row.dataset.drag !== 'true') return;
        if (t.closest('input,button,select,textarea,a,label,summary')) return;
        st = { row, x: e.clientX, y: e.clientY, active: false, keys: [] };
    });

    document.addEventListener('mousemove', e => {
        if (!st) return;
        if (!st.active) {
            if (Math.hypot(e.clientX - st.x, e.clientY - st.y) < 6) return;
            begin(e);
        }
        lastY = e.clientY;
        ghost.style.left = (e.clientX + 14) + 'px';
        ghost.style.top = (e.clientY + 14) + 'px';
        const z = zoneAt(e.clientX, e.clientY);
        if (z !== hot) {
            if (hot) hot.classList.remove('drop-hover');
            hot = z;
            if (z) z.classList.add('drop-hover');
        }
        e.preventDefault();
    });

    document.addEventListener('mouseup', e => finish(e, true));
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && st) finish(e, false); });
    window.addEventListener('blur', () => { if (st) finish({ clientX: -1, clientY: -1 }, false); });

    return { init: r => { ref = r; } };
})();
