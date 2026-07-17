(() => {
  const MARK = 'data-stopgap-enhanced';

  function api(url, options = {}) {
    return fetch(url, {
      headers: { 'content-type': 'application/json', ...(options.headers || {}) },
      ...options
    }).then(async response => {
      const text = await response.text();
      let payload = null;
      if (text) {
        try { payload = JSON.parse(text); }
        catch { payload = text; }
      }
      if (!response.ok) {
        const message = payload?.message || payload?.title || `${response.status} ${response.statusText}`;
        throw new Error(message);
      }
      return payload;
    });
  }

  function getLibraryId() {
    const value = document.getElementById('media-browser-library')?.value;
    const id = Number(value);
    return Number.isFinite(id) && id > 0 ? id : null;
  }

  function parsePathFromOnclick(button) {
    if (!button) return null;
    const text = button.getAttribute('onclick') || '';
    const match = text.match(/(?:openMediaBrowserPath|queueMediaBrowserFolderCleanup|queueMediaBrowserFolderHigh|setMediaBrowserFolderPriorityHigh)\((.*)\)/);
    if (!match) return null;
    let raw = match[1].trim();
    if (raw.endsWith(';')) raw = raw.slice(0, -1).trim();
    try { return JSON.parse(raw); }
    catch {
      return raw.replace(/^['"]|['"]$/g, '')
        .replace(/\\'/g, "'")
        .replace(/\\"/g, '"');
    }
  }

  function getCurrentPath() {
    const crumbs = document.getElementById('media-browser-breadcrumbs');
    const buttons = crumbs ? [...crumbs.querySelectorAll('button[onclick*="openMediaBrowserPath"]')] : [];
    if (!buttons.length) return '';
    return parsePathFromOnclick(buttons[buttons.length - 1]) || '';
  }

  function setMessage(message, kind = '') {
    const el = document.getElementById('media-browser-action-message');
    if (!el) return;
    el.textContent = message || '';
    el.className = `form-message ${kind}`.trim();
  }

  function formatResult(result) {
    const parts = [];
    if (result?.messages?.length) parts.push(...result.messages);
    parts.push(`Queued transcode: ${result?.queuedTranscode || 0}`);
    parts.push(`Already queued: ${result?.alreadyQueued || 0}`);
    parts.push(`Skipped: ${result?.skipped || 0}`);
    if (result?.failed) parts.push(`Failed: ${result.failed}`);
    if (result?.prioritizedQueuedJobs) parts.push(`Priority updates: ${result.prioritizedQueuedJobs}`);
    return parts.join('\n');
  }

  async function queueFolderTranscode(path = null, priority = 'Normal') {
    const libraryId = getLibraryId();
    if (!libraryId) {
      alert('Select a library first.');
      return;
    }

    const rootPath = path ?? getCurrentPath() ?? '';
    const scope = rootPath || 'entire selected library';
    const priorityText = priority === 'High' || priority === 'Urgent' ? ` as ${priority.toUpperCase()} priority` : '';
    if (!confirm(`Queue Transcode jobs${priorityText} under ${scope}?\n\nThis uses existing Transcode plans only. Files may need Reset/Replan first if they were planned before transcoding was enabled.`)) return;

    try {
      setMessage(`Queueing transcode work for ${scope}...`);
      const result = await api('/api/media/folder/queue', {
        method: 'POST',
        body: JSON.stringify({
          libraryId,
          path: rootPath,
          queueCleanup: false,
          queueTranscode: true,
          priority
        })
      });

      const summary = formatResult(result);
      alert(summary);
      setMessage(`Transcode queue complete · new ${result?.queuedTranscode || 0}, already ${result?.alreadyQueued || 0}, skipped ${result?.skipped || 0}`, result?.failed ? 'bad' : 'ok');
      if (typeof window.refreshMediaBrowser === 'function') await window.refreshMediaBrowser();
      if (typeof window.refreshMedia === 'function') await window.refreshMedia();
      if (typeof window.refreshJobs === 'function') await window.refreshJobs();
      if (typeof window.refreshStatus === 'function') await window.refreshStatus({ updateControls: false });
    } catch (error) {
      setMessage(`Transcode queue failed: ${error.message || error}`, 'bad');
      alert(error.message || error);
    }
  }

  window.queueMediaBrowserFolderTranscode = (path = null) => queueFolderTranscode(path, 'Normal');
  window.queueMediaBrowserFolderTranscodeHigh = (path = null) => queueFolderTranscode(path, 'High');

  function addCurrentFolderButtons() {
    const librarySelect = document.getElementById('media-browser-library');
    if (!librarySelect || document.getElementById('media-browser-queue-current-transcode')) return;

    const row = librarySelect.closest?.('.form-row') || librarySelect.parentElement;
    if (!row) return;
    row.classList.add('media-browser-toolbar-compact');

    const cleanupButton = document.getElementById('media-browser-queue-current-folder');
    if (cleanupButton) cleanupButton.textContent = 'Queue Cleanup';

    const allHighButton = document.getElementById('media-browser-queue-current-folder-high');
    if (allHighButton) allHighButton.textContent = 'Queue All High';

    const priorityButton = document.getElementById('media-browser-priority-current-folder');
    if (priorityButton) priorityButton.textContent = 'Priority High';

    const transcode = document.createElement('button');
    transcode.id = 'media-browser-queue-current-transcode';
    transcode.type = 'button';
    transcode.className = 'button';
    transcode.textContent = 'Queue Transcode';
    transcode.addEventListener('click', () => queueFolderTranscode(getCurrentPath(), 'Normal'));

    const transcodeHigh = document.createElement('button');
    transcodeHigh.id = 'media-browser-queue-current-transcode-high';
    transcodeHigh.type = 'button';
    transcodeHigh.className = 'button primary';
    transcodeHigh.textContent = 'Queue Transcode High';
    transcodeHigh.addEventListener('click', () => queueFolderTranscode(getCurrentPath(), 'High'));

    const message = document.getElementById('media-browser-action-message');
    if (message) {
      row.insertBefore(transcode, message);
      row.insertBefore(transcodeHigh, message);
    } else {
      row.appendChild(transcode);
      row.appendChild(transcodeHigh);
    }
  }

  function enhanceMediaBrowserRows() {
    const table = document.getElementById('media-browser-table');
    if (!table) return;

    table.classList.add('media-browser-table-compact');
    const rows = [...table.querySelectorAll('tbody tr')];
    for (const row of rows) {
      if (row.getAttribute(MARK) === '1') continue;
      const cells = row.querySelectorAll('td');
      if (cells.length < 2) continue;
      const isFolder = (cells[0].textContent || '').trim().toLowerCase().includes('folder');
      if (!isFolder) continue;

      const actionCell = cells[cells.length - 1];
      const openButton = actionCell.querySelector('button[onclick*="openMediaBrowserPath"]');
      const cleanupButton = actionCell.querySelector('button[onclick*="queueMediaBrowserFolderCleanup"]');
      const path = parsePathFromOnclick(openButton) ?? parsePathFromOnclick(cleanupButton);
      if (path == null) continue;

      // Make the folder/show/season name itself navigate; the Open button becomes unnecessary visual noise.
      const nameCell = cells[1];
      const label = nameCell.textContent.trim();
      nameCell.innerHTML = '';
      const link = document.createElement('button');
      link.type = 'button';
      link.className = 'link-button folder-link-button';
      link.textContent = label;
      link.addEventListener('click', () => {
        if (typeof window.openMediaBrowserPath === 'function') window.openMediaBrowserPath(path);
      });
      nameCell.appendChild(link);

      if (openButton) openButton.remove();

      if (cleanupButton) cleanupButton.textContent = 'Cleanup';
      const highButton = actionCell.querySelector('button[onclick*="queueMediaBrowserFolderHigh"]');
      if (highButton) highButton.textContent = 'All High';
      const priorityButton = actionCell.querySelector('button[onclick*="setMediaBrowserFolderPriorityHigh"]');
      if (priorityButton) priorityButton.textContent = 'Priority';

      const actionRow = actionCell.querySelector('.button-row') || actionCell;
      if (!actionCell.querySelector('[data-stopgap-queue-transcode]')) {
        const transcode = document.createElement('button');
        transcode.type = 'button';
        transcode.className = 'button';
        transcode.textContent = 'Transcode';
        transcode.setAttribute('data-stopgap-queue-transcode', '1');
        transcode.addEventListener('click', () => queueFolderTranscode(path, 'Normal'));
        actionRow.appendChild(transcode);
      }
      if (!actionCell.querySelector('[data-stopgap-queue-transcode-high]')) {
        const transcodeHigh = document.createElement('button');
        transcodeHigh.type = 'button';
        transcodeHigh.className = 'button primary';
        transcodeHigh.textContent = 'Transcode High';
        transcodeHigh.setAttribute('data-stopgap-queue-transcode-high', '1');
        transcodeHigh.addEventListener('click', () => queueFolderTranscode(path, 'High'));
        actionRow.appendChild(transcodeHigh);
      }

      row.setAttribute(MARK, '1');
    }
  }

  function enhance() {
    addCurrentFolderButtons();
    enhanceMediaBrowserRows();
  }

  const observer = new MutationObserver(() => {
    if (document.getElementById('page-media')?.classList.contains('active')) enhance();
  });

  document.addEventListener('DOMContentLoaded', () => {
    observer.observe(document.body, { childList: true, subtree: true });
    enhance();
    setInterval(enhance, 1500);
  });
})();
