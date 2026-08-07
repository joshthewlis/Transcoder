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

  // app.js keeps `state` as a top-level const rather than window.state. Read the visible
  // selector first so an auto-selected Movies library is also the library used by actions.
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
          // A cleaned/replaced item is still eligible for a later transcode phase.
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
      // Keep the old route for API compatibility; the server no longer performs a destructive reset.
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

  setInterval(() => {
    try {
      addMediaButtons();
      addReviewButtons();
      relabelLegacyResetButtons();
    } catch { }
  }, 500);

  // Override the legacy app.js action so the old Reset/Replan button cannot perform a reset,
  // even before the DOM relabel interval has run.
  window.resetReplanMedia = recalculateMedia;
  window.workflowReplanFolder = recalculateFolder;
  window.workflowQueueFolder = queueFolder;
  window.workflowApproveAllReviews = approveAllReviews;
  window.workflowRepairLimboReviews = repairLimboReviews;
})();
