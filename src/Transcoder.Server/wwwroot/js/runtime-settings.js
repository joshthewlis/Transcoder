(() => {
  const apiJson = async (url, options = {}) => {
    const response = await fetch(url, { headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }, ...options });
    const text = await response.text();
    const payload = text ? JSON.parse(text) : null;
    if (!response.ok) throw new Error(payload?.message || payload?.title || text || response.statusText);
    return payload;
  };

  const byId = (id) => document.getElementById(id);
  const value = (id) => byId(id)?.value ?? '';
  const checked = (id) => !!byId(id)?.checked;
  const setMessage = (message, kind = '') => { const el = byId('runtime-settings-message'); if (el) { el.textContent = message || ''; el.className = `form-message ${kind}`.trim(); } };
  const formatBytes = (bytes) => {
    const units = ['B', 'KB', 'MB', 'GB', 'TB']; let size = Number(bytes || 0); let i = 0;
    while (size >= 1024 && i < units.length - 1) { size /= 1024; i++; }
    return `${size.toFixed(size >= 10 || i === 0 ? 0 : 2)} ${units[i]}`;
  };

  const storagePayload = () => ({
    autoImportStorageMap: checked('rt-auto-import-storage-map'),
    autoImportStorageMapTime: value('rt-auto-import-time') || '08:10',
    autoImportStorageMapRetryMinutes: Number(value('rt-auto-import-retry') || 30),
    storageAwareScheduling: checked('rt-storage-aware'),
    maxActiveSourceJobsPerStorageKey: Number(value('rt-max-jobs-per-disk') || 1),
    preferLeastBusyStorageKey: checked('rt-prefer-least-busy'),
    useParentFolderForUnknownStorageKey: checked('rt-unknown-parent-folder'),
    unknownStorageKeyFolderDepth: Number(value('rt-unknown-folder-depth') || 2)
  });

  const safetyPayload = () => ({
    allowedSourceVideoCodecs: value('rt-allowed-source-codecs') || 'h264',
    reviewNonAllowedSourceCodecs: checked('rt-review-nonallowed-codecs'),
    reviewHdrSources: checked('rt-review-hdr')
  });

  const fillStorage = (s) => {
    byId('rt-auto-import-storage-map').checked = !!s.autoImportStorageMap;
    byId('rt-auto-import-time').value = s.autoImportStorageMapTime || '08:10';
    byId('rt-auto-import-retry').value = s.autoImportStorageMapRetryMinutes ?? 30;
    byId('rt-storage-aware').checked = !!s.storageAwareScheduling;
    byId('rt-max-jobs-per-disk').value = s.maxActiveSourceJobsPerStorageKey ?? 1;
    byId('rt-prefer-least-busy').checked = s.preferLeastBusyStorageKey !== false;
    byId('rt-unknown-parent-folder').checked = s.useParentFolderForUnknownStorageKey !== false;
    byId('rt-unknown-folder-depth').value = s.unknownStorageKeyFolderDepth ?? 2;
  };

  const fillSafety = (s) => {
    byId('rt-allowed-source-codecs').value = s.allowedSourceVideoCodecs || 'h264';
    byId('rt-review-nonallowed-codecs').checked = s.reviewNonAllowedSourceCodecs !== false;
    byId('rt-review-hdr').checked = s.reviewHdrSources !== false;
  };

  window.refreshRuntimeSettings = async () => {
    const [storage, safety] = await Promise.all([
      apiJson('/api/system/storage-settings'),
      apiJson('/api/system/transcode-safety')
    ]);
    fillStorage(storage);
    fillSafety(safety);
    setMessage('Runtime settings loaded.', 'ok');
  };

  window.saveRuntimeSettings = async () => {
    await apiJson('/api/system/storage-settings', { method: 'PUT', body: JSON.stringify(storagePayload()) });
    await apiJson('/api/system/transcode-safety', { method: 'PUT', body: JSON.stringify(safetyPayload()) });
    setMessage('Runtime settings saved. New queue/planning decisions use these values immediately.', 'ok');
  };

  window.importStorageMapNow = async () => {
    setMessage('Importing disk map...');
    const result = await apiJson('/api/system/storage-map/import', { method: 'POST' });
    setMessage(`Imported disk map: ${result.matchedMedia || 0} matched, ${result.unmatchedMedia || 0} unmatched.`, 'ok');
    await window.refreshStorageMapStatus?.();
  };

  window.refreshStorageMapStatus = async () => {
    const status = await apiJson('/api/system/storage-map/status');
    const lines = [`Mapped: ${status.totalMapped || 0}`, `Stale: ${status.totalStale || 0}`];
    for (const item of status.storageKeys || []) lines.push(`${item.storageKey}: ${item.count} file(s), ${formatBytes(item.sizeBytes || 0)}, stale ${item.staleCount || 0}`);
    const el = byId('rt-storage-map-status');
    if (el) el.textContent = lines.join('\n');
  };

  const inject = () => {
    if (byId('runtime-settings-panel')) return;
    const settings = byId('page-settings');
    if (!settings) return;

    const panel = document.createElement('section');
    panel.className = 'panel';
    panel.id = 'runtime-settings-panel';
    panel.innerHTML = `
      <div class="panel-header">
        <h2>Runtime Server Settings</h2>
        <div class="button-row">
          <button class="button primary" type="button" id="rt-save">Save Runtime Settings</button>
          <button class="button" type="button" id="rt-refresh">Refresh</button>
        </div>
      </div>

      <h3>Disk Map / Storage Scheduling</h3>
      <div class="form-grid">
        <label><input type="checkbox" id="rt-auto-import-storage-map"> Auto import disk map daily</label>
        <label>Import time <input id="rt-auto-import-time" type="time" value="08:10"></label>
        <label>Retry minutes <input id="rt-auto-import-retry" type="number" min="1" max="1440" value="30"></label>
        <label><input type="checkbox" id="rt-storage-aware"> Storage-aware scheduling</label>
        <label>Max active source jobs per disk <input id="rt-max-jobs-per-disk" type="number" min="1" max="32" value="1"></label>
        <label><input type="checkbox" id="rt-prefer-least-busy"> Prefer least busy disk</label>
        <label><input type="checkbox" id="rt-unknown-parent-folder"> Use parent folder bucket when disk is unknown</label>
        <label>Unknown folder depth <input id="rt-unknown-folder-depth" type="number" min="1" max="8" value="2"></label>
      </div>
      <div class="button-row">
        <button class="button" type="button" id="rt-import-map">Import Disk Map Now</button>
        <button class="button" type="button" id="rt-map-status">Disk Map Status</button>
      </div>
      <pre id="rt-storage-map-status" class="log-box">No storage map status loaded yet.</pre>

      <h3>Transcode Safety</h3>
      <div class="form-grid">
        <label>Allowed source codecs for automatic transcode <input id="rt-allowed-source-codecs" value="h264" placeholder="h264"></label>
        <label><input type="checkbox" id="rt-review-nonallowed-codecs"> Review non-allowed source codecs</label>
        <label><input type="checkbox" id="rt-review-hdr"> Review HDR sources</label>
      </div>
      <p class="section-help">Default behaviour: H264 sources may be transcoded to the selected target codec; non-H264 and HDR sources stop in Review first.</p>
      <span id="runtime-settings-message" class="form-message"></span>
    `;

    const firstPanel = settings.querySelector('section.panel');
    settings.insertBefore(panel, firstPanel || null);
    byId('rt-save')?.addEventListener('click', window.saveRuntimeSettings);
    byId('rt-refresh')?.addEventListener('click', window.refreshRuntimeSettings);
    byId('rt-import-map')?.addEventListener('click', window.importStorageMapNow);
    byId('rt-map-status')?.addEventListener('click', window.refreshStorageMapStatus);
    window.refreshRuntimeSettings().catch(error => setMessage(`Could not load runtime settings: ${error.message}`, 'bad'));
    window.refreshStorageMapStatus().catch(() => {});
  };

  const observer = new MutationObserver(inject);
  observer.observe(document.documentElement, { childList: true, subtree: true });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', inject);
  else inject();
})();
