(() => {
  const apiJson = async (url, options = {}) => {
    const response = await fetch(url, {
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
      ...options
    });
    const text = await response.text();
    const payload = text ? JSON.parse(text) : null;
    if (!response.ok) {
      const message = payload?.message || payload?.title || text || response.statusText;
      const error = new Error(message);
      error.status = response.status;
      error.payload = payload;
      throw error;
    }
    return payload;
  };

  const formatBytes = (bytes) => {
    const value = Number(bytes || 0);
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let size = value;
    let index = 0;
    while (size >= 1024 && index < units.length - 1) {
      size /= 1024;
      index++;
    }
    return `${size.toFixed(size >= 10 || index === 0 ? 0 : 2)} ${units[index]}`;
  };

  const getLibraryId = () => {
    const value = document.getElementById('media-browser-library')?.value;
    const parsed = Number.parseInt(value || '', 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
  };

  const decodeBrowserPathFromOnclick = (onclick) => {
    if (!onclick) return '';
    const match = onclick.match(/openMediaBrowserPath\((.*)\)\s*;?\s*return/i);
    if (!match) return '';
    const argument = match[1].trim();
    try {
      return JSON.parse(argument);
    } catch {
      return argument.replace(/^['"]|['"]$/g, '');
    }
  };

  const getCurrentPath = () => {
    if (typeof window.__manualTranscodeCurrentPath === 'string') {
      return window.__manualTranscodeCurrentPath;
    }

    const links = Array.from(document.querySelectorAll('#media-browser-breadcrumbs a'));
    const last = links.at(-1);
    return decodeBrowserPathFromOnclick(last?.getAttribute('onclick') || '');
  };

  const describeScope = () => getCurrentPath() || 'the selected library';

  const summarize = (result) => {
    const lines = [];
    if (result?.message) lines.push(result.message, '');
    lines.push(`Session: ${result.sessionId}`);
    lines.push(`Folder: ${result.path || '(library root)'}`);
    lines.push(`Tool: ${result.toolName || 'Manual'}`);
    lines.push(`Status: ${result.status || ''}`);
    lines.push(`Files captured: ${result.itemCount || 0}`);
    if (result.changedFiles || result.unchangedFiles || result.missingFiles || result.grewFiles) {
      lines.push(`Changed: ${result.changedFiles || 0}`);
      lines.push(`Unchanged: ${result.unchangedFiles || 0}`);
      lines.push(`Missing: ${result.missingFiles || 0}`);
      lines.push(`Grew: ${result.grewFiles || 0}`);
    }
    lines.push(`Before: ${formatBytes(result.beforeSizeBytes || 0)}`);
    if (result.afterSizeBytes) lines.push(`After: ${formatBytes(result.afterSizeBytes || 0)}`);
    lines.push(`Saved: ${formatBytes(result.savedBytes || 0)}`);
    if (result.jobsInserted) lines.push(`Completed Transcode jobs inserted: ${result.jobsInserted}`);
    if (result.probesQueued) lines.push(`Fresh probes queued: ${result.probesQueued}`);
    if (result.messages?.length) lines.push('', ...result.messages);
    return lines.join('\n');
  };

  const refreshAfterManualTranscode = async () => {
    try { if (typeof window.refreshMediaBrowser === 'function') await window.refreshMediaBrowser(); } catch { }
    try { if (typeof window.refreshMedia === 'function') await window.refreshMedia(); } catch { }
    try { if (typeof window.refreshJobs === 'function') await window.refreshJobs(); } catch { }
  };

  const setMessage = (message, kind = '') => {
    const el = document.getElementById('manual-transcode-message') || document.getElementById('media-browser-action-message');
    if (!el) return;
    el.textContent = message || '';
    el.className = `form-message ${kind}`.trim();
  };

  window.startManualTranscodeForCurrentFolder = async () => {
    const libraryId = getLibraryId();
    if (!libraryId) return alert('Select a library first.');
    const path = getCurrentPath();
    const scope = describeScope();
    const toolName = prompt(`External tool name for ${scope}:`, 'HandBrake');
    if (toolName === null) return;
    if (!confirm(`Start manual transcode baseline for ${scope}?\n\nThis captures current file sizes only. Replace the files externally after this, then click Finish Manual Transcode.`)) return;

    const body = { libraryId, path, toolName: toolName.trim() || 'HandBrake', replaceOpenSession: false };
    try {
      setMessage(`Capturing manual transcode baseline for ${scope}...`);
      const result = await apiJson('/api/manual-transcode/start', { method: 'POST', body: JSON.stringify(body) });
      setMessage(`Manual transcode baseline captured for ${result.itemCount || 0} file(s).`, 'ok');
      alert(summarize(result));
    } catch (error) {
      if (error.status === 409 && confirm(`${error.message}\n\nReplace the existing open session for this folder with a new baseline?`)) {
        body.replaceOpenSession = true;
        const result = await apiJson('/api/manual-transcode/start', { method: 'POST', body: JSON.stringify(body) });
        setMessage(`Manual transcode baseline replaced for ${result.itemCount || 0} file(s).`, 'ok');
        alert(summarize(result));
        return;
      }
      setMessage(`Manual transcode start failed: ${error.message}`, 'bad');
      alert(error.message || error);
    }
  };

  window.finishManualTranscodeForCurrentFolder = async () => {
    const libraryId = getLibraryId();
    if (!libraryId) return alert('Select a library first.');
    const path = getCurrentPath();
    const scope = describeScope();
    const queueFreshProbe = confirm(`Finish manual transcode for ${scope}?\n\nOK = update savings and queue fresh Probe jobs for changed files.\nCancel = update savings only, no probe jobs.`);
    if (!confirm(`This will compare current file sizes to the captured baseline, insert completed external Transcode jobs, and update Actual Saved. Continue?`)) return;

    try {
      setMessage(`Finishing manual transcode for ${scope}...`);
      const result = await apiJson('/api/manual-transcode/finish', {
        method: 'POST',
        body: JSON.stringify({ libraryId, path, queueFreshProbe })
      });
      setMessage(`Manual transcode finished · saved ${formatBytes(result.savedBytes || 0)}.`, 'ok');
      alert(summarize(result));
      await refreshAfterManualTranscode();
    } catch (error) {
      setMessage(`Manual transcode finish failed: ${error.message}`, 'bad');
      alert(error.message || error);
    }
  };

  window.showManualTranscodeStatusForCurrentFolder = async () => {
    const libraryId = getLibraryId();
    if (!libraryId) return alert('Select a library first.');
    const path = getCurrentPath();
    try {
      const url = `/api/manual-transcode/status?libraryId=${encodeURIComponent(libraryId)}&path=${encodeURIComponent(path || '')}`;
      const result = await apiJson(url);
      alert(summarize(result));
    } catch (error) {
      alert(error.message || error);
    }
  };

  window.cancelManualTranscodeForCurrentFolder = async () => {
    const libraryId = getLibraryId();
    if (!libraryId) return alert('Select a library first.');
    const path = getCurrentPath();
    const scope = describeScope();
    if (!confirm(`Cancel the open manual transcode session for ${scope}?\n\nThis does not change any media files or jobs.`)) return;
    try {
      const result = await apiJson('/api/manual-transcode/cancel', {
        method: 'POST',
        body: JSON.stringify({ libraryId, path, notes: 'Cancelled from Media Browser UI.' })
      });
      setMessage('Manual transcode session cancelled.', 'ok');
      alert(summarize(result));
    } catch (error) {
      setMessage(`Manual transcode cancel failed: ${error.message}`, 'bad');
      alert(error.message || error);
    }
  };

  const patchOpenMediaBrowserPath = () => {
    if (window.__manualTranscodePatchedOpenPath || typeof window.openMediaBrowserPath !== 'function') return;
    const original = window.openMediaBrowserPath;
    window.openMediaBrowserPath = async (path) => {
      window.__manualTranscodeCurrentPath = path || '';
      return await original(path);
    };
    window.__manualTranscodePatchedOpenPath = true;
  };

  const injectControls = () => {
    patchOpenMediaBrowserPath();
    const existing = document.getElementById('manual-transcode-start-current');
    if (existing) return;

    const anchor = document.getElementById('media-browser-action-message') || document.getElementById('media-browser-queue-current-folder-high') || document.getElementById('media-browser-library');
    if (!anchor?.parentElement) return;

    const start = document.createElement('button');
    start.id = 'manual-transcode-start-current';
    start.type = 'button';
    start.className = 'button';
    start.textContent = 'Start Manual Transcode';
    start.addEventListener('click', window.startManualTranscodeForCurrentFolder);

    const finish = document.createElement('button');
    finish.id = 'manual-transcode-finish-current';
    finish.type = 'button';
    finish.className = 'button primary';
    finish.textContent = 'Finish Manual Transcode';
    finish.addEventListener('click', window.finishManualTranscodeForCurrentFolder);

    const status = document.createElement('button');
    status.id = 'manual-transcode-status-current';
    status.type = 'button';
    status.className = 'button';
    status.textContent = 'Manual Status';
    status.addEventListener('click', window.showManualTranscodeStatusForCurrentFolder);

    const cancel = document.createElement('button');
    cancel.id = 'manual-transcode-cancel-current';
    cancel.type = 'button';
    cancel.className = 'button';
    cancel.textContent = 'Cancel Manual';
    cancel.addEventListener('click', window.cancelManualTranscodeForCurrentFolder);

    const message = document.createElement('span');
    message.id = 'manual-transcode-message';
    message.className = 'form-message';

    anchor.parentElement.insertBefore(start, anchor.nextSibling);
    anchor.parentElement.insertBefore(finish, start.nextSibling);
    anchor.parentElement.insertBefore(status, finish.nextSibling);
    anchor.parentElement.insertBefore(cancel, status.nextSibling);
    anchor.parentElement.insertBefore(message, cancel.nextSibling);
  };

  const observer = new MutationObserver(injectControls);
  observer.observe(document.documentElement, { childList: true, subtree: true });
  setInterval(injectControls, 1500);
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', injectControls);
  } else {
    injectControls();
  }
})();
