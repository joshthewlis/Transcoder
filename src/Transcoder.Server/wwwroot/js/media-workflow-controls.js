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

  const currentLibraryId = () => window.state?.mediaBrowser?.libraryId;
  const currentPath = () => window.state?.mediaBrowser?.path || '';
  const scopeText = () => currentPath() || 'entire selected library';

  async function replanFolder(queueAfterReplan = false, jobType = null) {
    const libraryId = currentLibraryId();
    if (!libraryId) return alert('Select a library first.');
    const scope = scopeText();
    const label = queueAfterReplan ? `replan and queue ${jobType || 'eligible work'} for ${scope}` : `replan ${scope}`;
    if (!confirm(`Run ${label}?\n\nThis clears stale cached plans for this scope and regenerates them from existing probe data. Running jobs are skipped.`)) return;
    msg(`Running ${label}...`);
    const result = await workflowApi('/api/media/workflow/replan-folder', {
      method: 'POST',
      body: JSON.stringify({ libraryId, path: currentPath(), includeFinal: false, queueAfterReplan, jobType })
    });
    const summary = (result.messages || []).join('\n') || `Considered ${result.considered || 0}, planned ${result.planned || 0}, queued ${result.queued || 0}.`;
    msg(summary, 'ok');
    alert(summary);
    await refresh();
  }

  async function queueFolder(jobType) {
    const libraryId = currentLibraryId();
    if (!libraryId) return alert('Select a library first.');
    const scope = scopeText();
    if (!confirm(`Queue ${jobType} for ${scope}?\n\nBlocked/stale items will be replanned first. Running jobs are left alone.`)) return;
    msg(`Queueing ${jobType} for ${scope}...`);
    const result = await workflowApi('/api/media/workflow/queue-folder', {
      method: 'POST',
      body: JSON.stringify({ libraryId, path: currentPath(), jobType, replanWhenBlocked: true })
    });
    const summary = (result.messages || []).join('\n') || `Queued ${result.queued || 0}, already ${result.alreadyQueued || 0}, skipped ${result.skipped || 0}, replanned ${result.replanned || 0}.`;
    msg(summary, result.queued ? 'ok' : 'warn');
    alert(summary);
    await refresh();
  }

  async function approveAllReviews() {
    if (!confirm('Approve ALL current Review items and hidden limbo reviews?\n\nThis is a bulk approval. Use it only when you are happy to approve the current plan warnings.')) return;
    const result = await workflowApi('/api/media/workflow/reviews/approve-all?includeLimbo=true', { method: 'POST' });
    const summary = (result.messages || []).join('\n') || `Approved ${result.approvedMediaItems || 0}; resolved ${result.resolvedReviewItems || 0}.`;
    alert(summary);
    await refresh();
  }

  async function repairLimboReviews() {
    const result = await workflowApi('/api/media/workflow/reviews/repair-limbo', { method: 'POST' });
    const summary = (result.messages || []).join('\n') || `Created ${result.created || 0} review item(s).`;
    alert(summary);
    await refresh();
  }

  function addMediaButtons() {
    const librarySelect = document.getElementById('media-browser-library');
    if (!librarySelect || document.getElementById('workflow-replan-folder')) return;
    const row = librarySelect.closest?.('.form-row') || librarySelect.parentElement;
    if (!row) return;

    const buttons = [
      ['workflow-replan-folder', 'Replan Current', () => replanFolder(false, null)],
      ['workflow-replan-queue-cleanup', 'Replan + Queue Cleanup', () => replanFolder(true, 'Cleanup')],
      ['workflow-replan-queue-transcode', 'Replan + Queue Transcode', () => replanFolder(true, 'Transcode')],
      ['workflow-queue-cleanup', 'Queue Cleanup Fixed', () => queueFolder('Cleanup')],
      ['workflow-queue-transcode', 'Queue Transcode Fixed', () => queueFolder('Transcode')]
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
    try { addMediaButtons(); addReviewButtons(); } catch { }
  }, 1000);

  window.workflowReplanFolder = replanFolder;
  window.workflowQueueFolder = queueFolder;
  window.workflowApproveAllReviews = approveAllReviews;
  window.workflowRepairLimboReviews = repairLimboReviews;
})();
