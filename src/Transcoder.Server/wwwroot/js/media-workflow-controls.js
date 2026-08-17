(() => {
  const workflowApi = async (url, options = {}) => {
    if (typeof api === 'function') return api(url, options);
    const response = await fetch(url, {
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
      ...options
    });
    if (!response.ok) throw new Error(await response.text());
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  };

  const msg = (text, kind = '') => {
    const el = document.getElementById('media-browser-action-message') || document.getElementById('workflow-action-message');
    if (!el) return;
    el.textContent = text || '';
    el.className = `form-message ${kind}`.trim();
  };

  const refresh = async () => {
    if (typeof refreshMediaBrowser === 'function') await refreshMediaBrowser();
    if (typeof refreshMedia === 'function') await refreshMedia();
    if (typeof refreshJobs === 'function') await refreshJobs();
    if (typeof refreshReview === 'function') await refreshReview();
  };

  const currentLibraryId = () => {
    const select = document.getElementById('media-browser-library');
    const selected = Number(select?.value || 0);
    if (selected > 0) return selected;
    const fromWindow = Number(window.state?.mediaBrowser?.libraryId || 0);
    return fromWindow > 0 ? fromWindow : null;
  };

  const currentPath = () => {
    if (window.state?.mediaBrowser?.path) return window.state.mediaBrowser.path;
    const breadcrumb = document.getElementById('media-browser-path');
    return breadcrumb?.dataset?.path || '';
  };

  const scopeText = () => currentPath() || 'entire selected library';

  async function recalculateFolder(queueAfterReplan = false, jobType = null) {
    const libraryId = currentLibraryId();
    if (!libraryId) return alert('Select a library first.');

    const scope = scopeText();
    const action = queueAfterReplan
      ? `recalculate plans and queue ${jobType || 'eligible work'} for ${scope}`
      : `recalculate plans for ${scope}`;

    if (!confirm(
      `Run ${action}?\n\n` +
      `Completed cleanup/transcode savings and processing history are preserved. ` +
      `Only future plans/reviews/queued work are recalculated. Running jobs are skipped.`
    )) return;

    msg(`Running ${action}...`);
    try {
      const result = await workflowApi('/api/media/workflow/replan-folder', {
        method: 'POST',
        body: JSON.stringify({
          libraryId,
          path: currentPath(),
          includeFinal: jobType === 'Transcode',
          queueAfterReplan,
          jobType
        })
      });

      const summary = (result.messages || []).join('\n') ||
        `Considered ${result.considered || 0}, planned ${result.planned || 0}, queued ${result.queued || 0}.`;
      msg(summary, 'ok');
      alert(summary);
      await refresh();
    } catch (error) {
      msg(error.message || String(error), 'bad');
      alert(error.message || error);
    }
  }

  async function queueFolder(jobType) {
    const libraryId = currentLibraryId();
    if (!libraryId) return alert('Select a library first.');

    const scope = scopeText();
    if (!confirm(
      `Queue ${jobType} for ${scope}?\n\n` +
      `Items with stale/incompatible plans will be recalculated safely first. ` +
      `Completed savings/history are never reset. Running jobs are left alone.`
    )) return;

    msg(`Queueing ${jobType} for ${scope}...`);
    try {
      const result = await workflowApi('/api/media/workflow/queue-folder', {
        method: 'POST',
        body: JSON.stringify({
          libraryId,
          path: currentPath(),
          jobType,
          replanWhenBlocked: true
        })
      });

      const summary = (result.messages || []).join('\n') ||
        `Queued ${result.queued || 0}, already ${result.alreadyQueued || 0}, skipped ${result.skipped || 0}, recalculated ${result.replanned || 0}.`;
      msg(summary, result.queued || result.replanned ? 'ok' : 'warn');
      alert(summary);
      await refresh();
    } catch (error) {
      msg(error.message || String(error), 'bad');
      alert(error.message || error);
    }
  }

  async function approveAllReviews() {
    if (!confirm(
      'Approve ALL current Review items and hidden limbo reviews?\n\n' +
      'Any library action waiting for PlanReview approval can continue automatically after approval.'
    )) return;

    const result = await workflowApi('/api/media/workflow/reviews/approve-all?includeLimbo=true', { method: 'POST' });
    const summary = (result.messages || []).join('\n') ||
      `Approved ${result.approvedMediaItems || 0}; resolved ${result.resolvedReviewItems || 0}.`;
    alert(summary);
    await refresh();
  }

  async function repairLimboReviews() {
    const result = await workflowApi('/api/media/workflow/reviews/repair-limbo', { method: 'POST' });
    const summary = (result.messages || []).join('\n') || `Created ${result.created || 0} review item(s).`;
    alert(summary);
    await refresh();
  }

  async function recalculateMedia(mediaId) {
    if (!confirm(
      'Recalculate this media item using the current policy?\n\n' +
      'Completed cleanup/transcode savings, processing history, and completed jobs are preserved.'
    )) return;

    try {
      const result = await workflowApi(`/api/media/${mediaId}/reset-replan`, { method: 'POST' });
      alert(result.message || 'Plan recalculated.');
      await refresh();
    } catch (error) {
      alert(error.message || error);
    }
  }

  function addMediaButtons() {
    const librarySelect = document.getElementById('media-browser-library');
    if (!librarySelect || document.getElementById('workflow-replan-folder')) return;

    const row = librarySelect.closest?.('.form-row') || librarySelect.parentElement;
    if (!row) return;

    const buttons = [
      ['workflow-replan-folder', 'Recalculate Current', () => recalculateFolder(false, null)],
      ['workflow-replan-queue-cleanup', 'Recalculate + Queue Cleanup', () => recalculateFolder(true, 'Cleanup')],
      ['workflow-replan-queue-transcode', 'Recalculate + Queue Transcode', () => recalculateFolder(true, 'Transcode')],
      ['workflow-queue-cleanup', 'Queue Cleanup', () => queueFolder('Cleanup')],
      ['workflow-queue-transcode', 'Queue Transcode', () => queueFolder('Transcode')]
    ];

    for (const [id, text, handler] of buttons) {
      const b = document.createElement('button');
      b.id = id;
      b.type = 'button';
      b.className = text.includes('Transcode') ? 'button primary' : 'button';
      b.textContent = text;
      b.addEventListener('click', handler);
      row.appendChild(b);
    }

    if (!document.getElementById('workflow-action-message')) {
      const span = document.createElement('span');
      span.id = 'workflow-action-message';
      span.className = 'form-message';
      row.appendChild(span);
    }
  }

  function relabelLegacyResetButtons() {
    document.querySelectorAll('button[onclick*="resetReplanMedia"]').forEach(button => {
      button.textContent = 'Recalculate Plan';
      button.title = 'Recalculate the future plan. Completed savings and processing history are preserved.';
    });
  }

  function addReviewButtons() {
    const table = document.getElementById('review-table');
    if (!table || document.getElementById('workflow-approve-all-reviews')) return;

    const container = table.parentElement || table;
    const row = document.createElement('div');
    row.className = 'button-row workflow-review-tools';
    row.innerHTML = `
      <button id="workflow-repair-limbo-reviews" type="button" class="button">Repair Missing Reviews</button>
      <button id="workflow-approve-all-reviews" type="button" class="button primary">Approve All Reviews</button>
    `;
    container.insertBefore(row, table);
    document.getElementById('workflow-repair-limbo-reviews')?.addEventListener('click', repairLimboReviews);
    document.getElementById('workflow-approve-all-reviews')?.addEventListener('click', approveAllReviews);
  }

  // -------------------------
  // Server activity / shutdown visibility
  // -------------------------

  const activityFormatBytes = bytes => {
    if (bytes === null || bytes === undefined || Number.isNaN(Number(bytes))) return 'Unknown';
    let value = Math.abs(Number(bytes));
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return `${value.toFixed(unit === 0 ? 0 : value >= 100 ? 1 : 2)} ${units[unit]}`;
  };

  const activityEscape = value => String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');

  const activityElapsed = seconds => {
    const total = Math.max(0, Math.floor(Number(seconds || 0)));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    return hours > 0
      ? `${hours}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
      : `${minutes}:${String(secs).padStart(2, '0')}`;
  };

  function createServerActivityPanel(id, heading) {
    const panel = document.createElement('section');
    panel.className = 'panel server-activity-panel';
    panel.id = id;
    panel.innerHTML = `
      <div class="panel-header">
        <h2>${heading}</h2>
        <span class="muted">Server-side staging/replacement I/O</span>
      </div>
      <div class="card-grid server-activity-summary"></div>
      <div class="server-activity-detail"></div>
    `;
    return panel;
  }

  function ensureServerActivityPanels() {
    const dashboard = document.getElementById('page-dashboard');
    if (dashboard) {
      const badges = document.getElementById('status-cards');
      const libraryOverview = document.getElementById('dashboard-library-overview')?.closest('.panel');

      // Requested dashboard order:
      // badges -> Library Overview -> Server Activity -> Active Jobs -> Finished Jobs.
      if (badges && libraryOverview && badges.nextElementSibling !== libraryOverview)
        badges.insertAdjacentElement('afterend', libraryOverview);

      if (!document.getElementById('dashboard-server-activity')) {
        const panel = createServerActivityPanel('dashboard-server-activity', 'Server Activity');
        if (libraryOverview)
          libraryOverview.insertAdjacentElement('afterend', panel);
        else if (badges)
          badges.insertAdjacentElement('afterend', panel);
      }
    }

    const workersPage = document.getElementById('page-workers');
    if (workersPage && !document.getElementById('workers-server-activity')) {
      const workersPanel = document.getElementById('workers-table')?.closest('.panel');
      const panel = createServerActivityPanel('workers-server-activity', 'Server Activity / Shutdown Safety');
      if (workersPanel)
        workersPanel.insertAdjacentElement('beforebegin', panel);
      else
        workersPage.appendChild(panel);
    }
  }

  function renderServerActivityInto(panel, data) {
    if (!panel || !data) return;
    const summary = panel.querySelector('.server-activity-summary');
    const detail = panel.querySelector('.server-activity-detail');

    const safe = data.safeToStop
      ? '<span class="status ok">Server I/O idle · safe to stop</span>'
      : '<span class="status warn">Server I/O active · WAIT</span>';

    const replaceMode = data.autoReplaceEnabled
      ? '<span class="status ok">Auto Replace enabled</span>'
      : '<span class="status">Auto Replace off</span>';

    if (summary) {
      summary.innerHTML = `
        <div class="card"><div class="card-title">Server I/O</div><div class="card-value">${safe}</div></div>
        <div class="card"><div class="card-title">Replace Mode</div><div class="card-value">${replaceMode}</div></div>
        <div class="card"><div class="card-title">Staged Waiting</div><div class="card-value">${Number(data.stagedWaiting || 0)}</div></div>
        <div class="card"><div class="card-title">Replace Failures</div><div class="card-value">${Number(data.replaceFailures || 0)}</div></div>
        <div class="card"><div class="card-title">Replace-enabled Libraries</div><div class="card-value">${Number(data.replaceEnabledLibraries || 0)}</div></div>
      `;
    }

    const op = data.current;
    if (op) {
      const media = op.relativePath || (op.mediaId ? `Media ${op.mediaId}` : 'Unknown media');
      const size = op.totalBytes == null ? '' : ` · ${activityFormatBytes(op.totalBytes)}`;
      const paths = [
        op.originalPath ? `<div><small><strong>Original:</strong> ${activityEscape(op.originalPath)}</small></div>` : '',
        op.stagingPath ? `<div><small><strong>Staging:</strong> ${activityEscape(op.stagingPath)}</small></div>` : ''
      ].join('');

      detail.innerHTML = `
        <div class="mode-warning">
          <strong>${activityEscape(op.stage || op.operation || 'Server work')}</strong>
          · ${activityEscape(op.libraryName || '')}
          · ${activityEscape(media)}${size}
          · elapsed ${activityElapsed(op.elapsedSeconds)}
          <div>${activityEscape(op.message || '')}</div>
          ${paths}
        </div>
      `;
      return;
    }

    const last = data.last;
    const lastText = last
      ? `Last: ${last.success ? 'completed' : 'stopped/skipped'} ${activityEscape(last.relativePath || last.operation || 'server operation')} · ${activityEscape(last.message || '')}`
      : 'No server-side replacement activity has been recorded since this server process started.';

    detail.innerHTML = `
      <p class="muted">
        ${lastText}<br>
        ${activityEscape(data.activeHoursMessage || '')}
        ${Number(data.stagedWaiting || 0) > 0 && data.autoReplaceEnabled
          ? '<br><strong>Note:</strong> staged items are waiting and the server may start replacement on the next auto-replace cycle.'
          : ''}
      </p>
    `;
  }

  async function refreshServerActivity() {
    ensureServerActivityPanels();

    const dashboardPanel = document.getElementById('dashboard-server-activity');
    const workersPanel = document.getElementById('workers-server-activity');
    if (!dashboardPanel && !workersPanel) return;

    try {
      const data = await workflowApi('/api/server/activity');
      renderServerActivityInto(dashboardPanel, data);
      renderServerActivityInto(workersPanel, data);
    } catch (error) {
      const html = `<p class="text-bad">Server activity unavailable: ${activityEscape(error.message || error)}</p>`;
      dashboardPanel?.querySelector('.server-activity-detail')?.replaceChildren();
      workersPanel?.querySelector('.server-activity-detail')?.replaceChildren();
      if (dashboardPanel?.querySelector('.server-activity-detail'))
        dashboardPanel.querySelector('.server-activity-detail').innerHTML = html;
      if (workersPanel?.querySelector('.server-activity-detail'))
        workersPanel.querySelector('.server-activity-detail').innerHTML = html;
    }
  }

  setInterval(() => {
    try {
      addMediaButtons();
      addReviewButtons();
      relabelLegacyResetButtons();
      ensureServerActivityPanels();
    } catch { }
  }, 500);

  // Server activity uses the same rough cadence as the main UI polling.
  setInterval(() => {
    refreshServerActivity().catch(() => {});
  }, 3000);

  window.addEventListener('hashchange', () => {
    setTimeout(() => {
      ensureServerActivityPanels();
      refreshServerActivity().catch(() => {});
    }, 50);
  });

  window.resetReplanMedia = recalculateMedia;
  window.workflowReplanFolder = recalculateFolder;
  window.workflowQueueFolder = queueFolder;
  window.workflowApproveAllReviews = approveAllReviews;
  window.workflowRepairLimboReviews = repairLimboReviews;
  window.refreshServerActivity = refreshServerActivity;

  ensureServerActivityPanels();
  refreshServerActivity().catch(() => {});
})();
