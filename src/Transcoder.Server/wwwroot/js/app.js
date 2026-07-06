const state = {
  page: location.hash?.replace('#', '') || 'dashboard',
  refreshMs: 3000,
  mediaBrowser: { libraryId: null, path: '' },
  timer: null,
  integrations: [],
  libraries: [],
  librariesById: new Map(),
  processingMode: null,
  jobFilters: { jobType: '', status: '', workerId: '', activeOnly: false },
  formDirty: false,
  paging: {
    jobs: { page: 1, pageSize: 50, totalCount: 0 },
    media: { page: 1, pageSize: 50, totalCount: 0 },
    review: { page: 1, pageSize: 50, totalCount: 0 }
  }
};

const api = async (url, options = {}) => {
  const response = await fetch(url, {
    headers: { 'content-type': 'application/json', ...(options.headers || {}) },
    ...options
  });
  const text = await response.text();
  const parseJson = () => {
    if (!text) return null;
    try { return JSON.parse(text); } catch { return text; }
  };
  const payload = parseJson();
  if (!response.ok) {
    const message = payload?.message || payload?.title || `${response.status} ${response.statusText}`;
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return payload;
};

function setPage(page) {
  state.page = page || 'dashboard';
  document.querySelectorAll('.page').forEach(el => el.classList.remove('active'));
  document.querySelector(`#page-${state.page}`)?.classList.add('active');
  document.querySelectorAll('.nav-link').forEach(el => el.classList.toggle('active', el.dataset.page === state.page));
  refreshAll();
}

window.addEventListener('hashchange', () => setPage(location.hash.replace('#', '')));
document.querySelectorAll('.nav-link').forEach(el => el.addEventListener('click', () => setPage(el.dataset.page)));
document.querySelectorAll('[data-refresh]').forEach(el => el.addEventListener('click', () => refreshAll({ force: true })));

document.getElementById('save-processing-mode')?.addEventListener('click', async () => {
  const processingMode = document.getElementById('processing-mode-select').value;
  await api('/api/system/mode', { method: 'PUT', body: JSON.stringify({ processingMode }) });
  state.formDirty = false;
  await refreshStatus();
});

document.getElementById('save-execution-settings')?.addEventListener('click', saveExecutionSettings);
document.getElementById('media-browser-library')?.addEventListener('change', event => {
  state.mediaBrowser.libraryId = event.target.value ? Number(event.target.value) : null;
  state.mediaBrowser.path = '';
  refreshMediaBrowser();
});
document.getElementById('media-browser-up')?.addEventListener('click', async () => {
  if (!state.mediaBrowser.path) return;
  const parts = state.mediaBrowser.path.split('/').filter(Boolean);
  parts.pop();
  state.mediaBrowser.path = parts.join('/');
  await refreshMediaBrowser();
});

document.getElementById('create-integration-form')?.addEventListener('submit', createIntegration);
document.getElementById('test-integration-form')?.addEventListener('click', testIntegrationForm);
document.getElementById('apply-job-filters')?.addEventListener('click', applyJobFilters);
document.getElementById('reset-job-filters')?.addEventListener('click', resetJobFilters);
document.getElementById('clear-finished-jobs')?.addEventListener('click', clearFinishedJobs);
document.getElementById('create-library-form')?.addEventListener('submit', createLibrary);
document.getElementById('reset-library-form')?.addEventListener('click', () => {
  defaultLibraryFormValues();
  setLibraryMessage('');
});

document.getElementById('close-plan-modal')?.addEventListener('click', closePlanModal);
document.getElementById('plan-modal')?.addEventListener('click', event => {
  if (event.target?.id === 'plan-modal') closePlanModal();
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closePlanModal();
});

document.querySelectorAll('[data-page-size]').forEach(el => el.addEventListener('change', () => {
  const key = el.dataset.pageSize;
  if (!state.paging[key]) return;
  state.paging[key].pageSize = parseInt(el.value, 10) || 50;
  state.paging[key].page = 1;
  refreshAll();
}));

document.querySelectorAll('[data-page-action]').forEach(el => el.addEventListener('click', () => {
  const key = el.dataset.pageTarget;
  const pager = state.paging[key];
  if (!pager) return;
  const maxPage = Math.max(1, Math.ceil((pager.totalCount || 0) / pager.pageSize));
  if (el.dataset.pageAction === 'prev') pager.page = Math.max(1, pager.page - 1);
  if (el.dataset.pageAction === 'next') pager.page = Math.min(maxPage, pager.page + 1);
  refreshAll();
}));

document.addEventListener('input', event => {
  if (event.target.closest?.('.policy-form, .settings-panel, #jobs-table, .filter-bar')) state.formDirty = true;
});
document.addEventListener('change', event => {
  if (event.target.closest?.('.policy-form, .settings-panel, #jobs-table, .filter-bar')) state.formDirty = true;
});

function isUserEditingPage() {
  const active = document.activeElement;
  if (active && ['INPUT', 'SELECT', 'TEXTAREA'].includes(active.tagName)) return true;
  return state.formDirty && ['libraries', 'settings', 'integrations', 'jobs'].includes(state.page);
}

function renderTable(tableId, columns, rows) {
  const table = document.getElementById(tableId);
  if (!table) return;
  const head = `<thead><tr>${columns.map(c => `<th>${c.title}</th>`).join('')}</tr></thead>`;
  const bodyRows = rows.length ? rows.map(row => `<tr>${columns.map(c => `<td>${c.render ? c.render(row) : row[c.key] ?? ''}</td>`).join('')}</tr>`).join('') : `<tr><td colspan="${columns.length}">No records.</td></tr>`;
  table.innerHTML = `${head}<tbody>${bodyRows}</tbody>`;
}

function updatePager(key, result) {
  const pager = state.paging[key];
  if (!pager || !result) return;
  pager.page = result.page ?? pager.page;
  pager.pageSize = result.pageSize ?? pager.pageSize;
  pager.totalCount = result.totalCount ?? 0;
  const maxPage = Math.max(1, Math.ceil(pager.totalCount / pager.pageSize));
  const info = document.getElementById(`${key}-page-info`);
  if (info) info.textContent = `Page ${pager.page} of ${maxPage} · ${pager.totalCount} total`;
  const size = document.getElementById(`${key}-page-size`);
  if (size && `${pager.pageSize}` !== size.value) size.value = `${pager.pageSize}`;
  document.querySelectorAll(`[data-page-target="${key}"]`).forEach(button => {
    if (button.dataset.pageAction === 'prev') button.disabled = pager.page <= 1;
    if (button.dataset.pageAction === 'next') button.disabled = pager.page >= maxPage;
  });
}

function statusClass(value) {
  if (['Online', 'Completed', 'Probed', 'Staged', 'StagedCleaned', 'ReplacedCleaned', 'ReplacedTranscoded'].includes(value)) return 'ok';
  if (['PartialPathAccess', 'Queued', 'Leased', 'Running', 'NeedsReview', 'Unresponsive', 'Planning', 'ReadyToCleanup', 'Cleaning', 'ReadyToTranscode', 'Transcoding'].includes(value)) return 'warn';
  if (['Failed', 'PathCheckFailed', 'RequirementsFailed', 'Lost', 'Cancelled', 'ReplaceFailed'].includes(value)) return 'bad';
  return '';
}

const badge = value => `<span class="status ${statusClass(value)}">${escapeHtml(value ?? '')}</span>`;
const date = value => value ? new Date(value).toLocaleString() : '';

function formatBytes(bytes) {
  if (bytes === null || bytes === undefined || Number.isNaN(Number(bytes))) return 'Unknown';
  const value = Number(bytes);
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = Math.abs(value);
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit += 1; }
  const sign = value < 0 ? '-' : '';
  const decimals = unit === 0 ? 0 : size >= 100 ? 1 : 2;
  return `${sign}${size.toFixed(decimals)} ${units[unit]}`;
}

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}

function escapeAttribute(value) {
  return escapeHtml(value).replaceAll('`', '&#096;');
}

function numberOrDefault(value, fallback) {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function optionalInt(value) {
  const parsed = parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function csv(value) {
  return (value || '')
    .split(',')
    .map(x => x.trim())
    .filter(Boolean);
}

function integrationOptions(type, selectedId = null) {
  const selected = selectedId == null ? '' : String(selectedId);
  const items = (state.integrations || [])
    .filter(x => !type || x.integrationType === type)
    .filter(x => x.enabled);
  return `<option value="">None</option>` + items.map(x => {
    const value = String(x.id);
    const label = `${x.name} (#${x.id})`;
    return `<option value="${escapeAttribute(value)}" ${value === selected ? 'selected' : ''}>${escapeHtml(label)}</option>`;
  }).join('');
}

function updateIntegrationSelects() {
  const radarr = document.getElementById('library-radarr-integration-id');
  const sonarr = document.getElementById('library-sonarr-integration-id');
  if (radarr) {
    const selected = radarr.value;
    radarr.innerHTML = integrationOptions('Radarr', selected);
  }
  if (sonarr) {
    const selected = sonarr.value;
    sonarr.innerHTML = integrationOptions('Sonarr', selected);
  }
}

async function loadIntegrations() {
  state.integrations = await api('/api/integrations');
  updateIntegrationSelects();
  return state.integrations;
}

function defaultLibraryFormValues() {
  document.getElementById('library-name').value = 'Test Movies';
  document.getElementById('library-root').value = 'C:\\TransTest\\Movies';
  document.getElementById('library-processing-strategy').value = 'CleanupOnly';
  document.getElementById('library-target-codec').value = 'hevc';
  document.getElementById('library-video-profile').value = 'hevc_nvenc_balanced';
  document.getElementById('library-transcode-engine').value = 'PreferGpu';
  document.getElementById('library-convert-codecs').value = 'h264';
  document.getElementById('library-skip-codecs').value = 'hevc,av1';
  document.getElementById('library-audio-languages').value = 'eng';
  document.getElementById('library-subtitle-languages').value = 'eng';
  document.getElementById('library-audio-duplicate-mode').value = 'KeepBestPerLanguage';
  document.getElementById('library-premium-audio-mode').value = 'KeepBestPremiumAndCompatibility';
  document.getElementById('library-premium-audio-ranking').value = 'PreferAtmosThenTrueHd';
  document.getElementById('library-preferred-audio-channels').value = '6';
  document.getElementById('library-maximum-audio-channels').value = '8';
  document.getElementById('library-metadata-priority').value = 'Radarr,Sonarr,Tmdb,FileProbe';
  document.getElementById('library-radarr-integration-id').value = '';
  document.getElementById('library-sonarr-integration-id').value = '';
  document.getElementById('library-default-original-language').value = '';
  document.getElementById('library-audio-target').value = 'copy';
  document.getElementById('library-unknown-audio').value = 'NeedsReview';
  document.getElementById('library-unknown-subtitles').value = 'NeedsReview';
  document.getElementById('library-enabled').checked = true;
  document.getElementById('library-keep-original-language').checked = true;
  document.getElementById('library-original-language-first').checked = true;
  document.getElementById('library-preserve-spatial-audio').checked = true;
  document.getElementById('library-preserve-lossless-audio').checked = true;
  document.getElementById('library-keep-compatibility-track').checked = true;
  document.getElementById('library-review-different-mix-titles').checked = true;
  document.getElementById('library-remove-descriptive-audio').checked = true;
  document.getElementById('library-keep-forced').checked = true;
  document.getElementById('library-watch-enabled').checked = true;
  document.getElementById('library-scan-on-startup').checked = false;
  document.getElementById('library-watch-settle').value = '300';
  document.getElementById('library-periodic-rescan').value = '120';
  document.getElementById('library-scan-after-create').checked = true;
  document.getElementById('library-generate-cleanup-jobs').checked = true;
  document.getElementById('library-generate-transcode-jobs').checked = false;
  document.getElementById('library-replace-originals').checked = false;
}

function setLibraryMessage(message, kind = '') {
  const el = document.getElementById('library-form-message');
  if (!el) return;
  el.textContent = message || '';
  el.className = `form-message ${kind}`.trim();
}

function buildLibraryRequest() {
  return {
    name: document.getElementById('library-name').value.trim(),
    rootPath: document.getElementById('library-root').value.trim(),
    enabled: document.getElementById('library-enabled').checked,
    policy: {
      processingStrategy: document.getElementById('library-processing-strategy').value,
      execution: {
        generateCleanupJobs: document.getElementById('library-generate-cleanup-jobs').checked,
        generateTranscodeJobs: document.getElementById('library-generate-transcode-jobs').checked
      },
      video: {
        targetCodec: document.getElementById('library-target-codec').value,
        profile: document.getElementById('library-video-profile').value,
        transcodeEngine: document.getElementById('library-transcode-engine').value,
        onlyConvertCodecs: csv(document.getElementById('library-convert-codecs').value),
        skipCodecs: csv(document.getElementById('library-skip-codecs').value)
      },
      audio: {
        keepLanguages: csv(document.getElementById('library-audio-languages').value),
        keepOriginalLanguage: document.getElementById('library-keep-original-language').checked,
        originalLanguageFirst: document.getElementById('library-original-language-first').checked,
        fallbackToDefault: true,
        fallbackToFirst: true,
        targetCodec: document.getElementById('library-audio-target').value,
        targetProfile: null,
        removeCommentary: true,
        removeDescriptiveAudio: document.getElementById('library-remove-descriptive-audio').checked,
        duplicateMode: document.getElementById('library-audio-duplicate-mode').value,
        premiumMode: document.getElementById('library-premium-audio-mode').value,
        premiumRanking: document.getElementById('library-premium-audio-ranking').value,
        preferredChannels: numberOrDefault(document.getElementById('library-preferred-audio-channels').value, 6),
        maximumChannels: numberOrDefault(document.getElementById('library-maximum-audio-channels').value, 8),
        preserveSpatialAudio: document.getElementById('library-preserve-spatial-audio').checked,
        preserveLosslessAudio: document.getElementById('library-preserve-lossless-audio').checked,
        keepCompatibilityTrack: document.getElementById('library-keep-compatibility-track').checked,
        reviewDifferentMixTitles: document.getElementById('library-review-different-mix-titles').checked,
        unknownAudioAction: document.getElementById('library-unknown-audio').value
      },
      subtitles: {
        keepLanguages: csv(document.getElementById('library-subtitle-languages').value),
        keepOriginalLanguage: false,
        keepForced: document.getElementById('library-keep-forced').checked,
        unknownSubtitleAction: document.getElementById('library-unknown-subtitles').value
      },
      output: {
        stagingOnly: true,
        mirrorFolderStructure: true,
        replaceOriginals: document.getElementById('library-replace-originals').checked
      },
      review: {
        planReviewRequired: true,
        humanReviewRequiredForUnknownMetadata: true
      },
      metadata: {
        metadataSourcePriority: csv(document.getElementById('library-metadata-priority').value),
        radarrIntegrationId: optionalInt(document.getElementById('library-radarr-integration-id').value),
        sonarrIntegrationId: optionalInt(document.getElementById('library-sonarr-integration-id').value),
        useTmdb: true,
        matchByPath: true,
        defaultOriginalLanguage: document.getElementById('library-default-original-language').value.trim() || null,
        inferOriginalLanguageFromSingleAudioLanguage: true,
        requireOriginalLanguageWhenPolicyUsesIt: true
      },
      watch: {
        enabled: document.getElementById('library-watch-enabled').checked,
        includeSubdirectories: true,
        scanOnStartup: document.getElementById('library-scan-on-startup').checked,
        settleSeconds: parseInt(document.getElementById('library-watch-settle').value, 10) || 300,
        minimumFileAgeSeconds: 120,
        periodicRescanMinutes: parseInt(document.getElementById('library-periodic-rescan').value, 10) || 0,
        ignoreTemporaryExtensions: ['.part', '.partial', '.tmp', '.download', '.!qb', '.crdownload']
      },
      ignoreFolderNames: ['.transcoder', '@eaDir', 'lost+found', 'sample', 'samples'],
      allowedExtensions: ['.mkv', '.mp4', '.m4v', '.avi', '.mov', '.ts', '.m2ts']
    }
  };
}

async function createLibrary(event) {
  event.preventDefault();
  const request = buildLibraryRequest();
  if (!request.name || !request.rootPath) {
    setLibraryMessage('Name and root path are required.', 'bad');
    return;
  }

  setLibraryMessage('Creating library...');
  try {
    const library = await api('/api/libraries', { method: 'POST', body: JSON.stringify(request) });
    if (document.getElementById('library-scan-after-create').checked) {
      await api(`/api/libraries/${library.id}/scan`, { method: 'POST', body: JSON.stringify({ force: false, createProbeJobs: true }) });
      setLibraryMessage(`Created ${library.name} and queued scan.`, 'ok');
    } else {
      setLibraryMessage(`Created ${library.name}.`, 'ok');
    }
    await refreshLibraries();
    state.formDirty = false;
    await refreshStatus();
  } catch (error) {
    console.error(error);
    setLibraryMessage(`Create failed: ${error.message}`, 'bad');
  }
}



function buildIntegrationRequest() {
  const integrationPath = document.getElementById('integration-path')?.value.trim() || '';
  const serverPath = document.getElementById('integration-server-path')?.value.trim() || '';
  return {
    name: document.getElementById('integration-name').value.trim(),
    integrationType: document.getElementById('integration-type').value,
    enabled: document.getElementById('integration-enabled').checked,
    baseUrl: document.getElementById('integration-base-url').value.trim(),
    apiKey: document.getElementById('integration-api-key').value.trim() || null,
    pathMappings: integrationPath && serverPath ? [{ integrationPath, serverPath }] : []
  };
}

async function createIntegration(event) {
  event.preventDefault();
  const request = buildIntegrationRequest();
  const message = document.getElementById('integration-form-message');
  if (!request.name || (!request.baseUrl && request.integrationType !== 'Tmdb')) {
    if (message) message.textContent = 'Name and base URL are required.';
    return;
  }
  try {
    await api('/api/integrations', { method: 'POST', body: JSON.stringify(request) });
    if (message) { message.textContent = 'Integration saved.'; message.className = 'form-message ok'; }
    document.getElementById('integration-api-key').value = '';
    state.formDirty = false;
    await refreshIntegrations();
  } catch (error) {
    if (message) { message.textContent = `Save failed: ${error.message}`; message.className = 'form-message bad'; }
  }
}

async function testIntegrationForm() {
  const message = document.getElementById('integration-form-message');
  const request = buildIntegrationRequest();
  if (!request.name) request.name = 'Unsaved integration test';
  if (!request.baseUrl && request.integrationType !== 'Tmdb') {
    if (message) { message.textContent = 'Base URL is required before testing.'; message.className = 'form-message bad'; }
    return;
  }
  try {
    if (message) { message.textContent = 'Testing integration...'; message.className = 'form-message'; }
    const result = await api('/api/integrations/test', { method: 'POST', body: JSON.stringify(request) });
    if (message) {
      message.textContent = `${result.success ? 'Test OK' : 'Test failed'}: ${result.message}${result.version ? ` · ${result.version}` : ''}`;
      message.className = `form-message ${result.success ? 'ok' : 'bad'}`;
    }
  } catch (error) {
    if (message) { message.textContent = `Test failed: ${error.message}`; message.className = 'form-message bad'; }
  }
}

async function refreshIntegrations() {
  const integrations = await loadIntegrations();
  const table = document.getElementById('integrations-table');
  if (!table) return;
  renderTable('integrations-table', [
    { title: 'ID', key: 'id' },
    { title: 'Name', key: 'name' },
    { title: 'Type', key: 'integrationType' },
    { title: 'Enabled', render: i => i.enabled ? badge('Enabled') : badge('Disabled') },
    { title: 'Base URL', key: 'baseUrl' },
    { title: 'API Key', render: i => i.hasApiKey ? badge('Set') : badge('Missing') },
    { title: 'Mappings', render: i => (i.pathMappings || []).map(m => `${escapeHtml(m.integrationPath)} → ${escapeHtml(m.serverPath)}`).join('<br>') },
    { title: 'Actions', render: i => `<div class="button-row"><button class="button" onclick="testIntegration(${i.id})">Test</button><button class="button" onclick="deleteIntegration(${i.id})">Delete</button></div>` }
  ], integrations);
}

window.testIntegration = async (integrationId) => {
  const result = await api(`/api/integrations/${integrationId}/test`, { method: 'POST' });
  alert(`${result.success ? 'OK' : 'Failed'}: ${result.message}${result.version ? `\nVersion: ${result.version}` : ''}`);
};

window.deleteIntegration = async (integrationId) => {
  if (!confirm('Delete this integration?')) return;
  await api(`/api/integrations/${integrationId}`, { method: 'DELETE' });
  await refreshIntegrations();
};

window.refreshLibraryMetadata = async (libraryId) => {
  await api(`/api/libraries/${libraryId}/metadata/refresh?force=true`, { method: 'POST' });
  await refreshLibraries();
  await refreshMedia();
};

window.refreshMediaMetadata = async (mediaId) => {
  await api(`/api/media/${mediaId}/metadata/refresh?force=true`, { method: 'POST' });
  await refreshMedia();
};

async function refreshStatus(options = {}) {
  const status = await api('/api/system/status');
  state.processingMode = status.processingMode;
  document.getElementById('server-version').textContent = `v${status.serverVersion} · ${formatProcessingMode(status.processingMode)}`;
  if (options.updateControls !== false) {
    document.getElementById('processing-mode-select').value = status.processingMode;
    updateProcessingModeWarning(status.processingMode);
    updateExecutionSettingsControls(status);
  }
  document.getElementById('status-cards').innerHTML = [
    ['Mode', formatProcessingMode(status.processingMode)],
    ['Queued Jobs', status.queuedJobs],
    ['Running Jobs', status.runningJobs],
    ['Actual Saved', formatBytes(status.totalActualSavedBytes || 0)],
    ['Cleanup Done', status.completedCleanupCount || 0],
    ['Transcode Done', status.completedTranscodeCount || 0],
    ['Automation', `${status.autoQueueCleanupJobs ? 'Cleanup' : ''}${status.autoQueueCleanupJobs && status.autoQueueTranscodeJobs ? ' + ' : ''}${status.autoQueueTranscodeJobs ? 'Transcode' : ''}` || 'Manual'],
    ['Active Hours', formatActiveHoursSummary(status.activeHours)],
    ['Workers Online', status.workersOnline],
    ['Workers Lost', status.workersLost],
    ['Needs Review', status.needsReview]
  ].map(([title, value]) => `<div class="card"><div class="card-title">${title}</div><div class="card-value">${value}</div></div>`).join('');
}

function formatProcessingMode(mode) {
  const labels = {
    Disabled: 'Disabled',
    ScanOnly: 'Scan only',
    ProbeOnly: 'Probe only',
    PlanOnly: 'Plan only',
    PlanAndReview: 'Plan and review only',
    TranscodeToStaging: 'Run staged work',
    ReplaceApproved: 'Replace approved originals'
  };
  return labels[mode] || mode || '';
}

function updateProcessingModeWarning(mode) {
  const el = document.getElementById('processing-mode-warning');
  if (!el) return;
  if (mode === 'ReplaceApproved') {
    el.hidden = false;
    el.innerHTML = `<strong>Replace mode is enabled.</strong> Queued Cleanup/Transcode jobs may run to staging, and libraries with Allow replace enabled can replace originals automatically after staging completes.`;
    return;
  }
  if (mode === 'TranscodeToStaging') {
    el.hidden = false;
    el.innerHTML = `<strong>Run staged work is enabled.</strong> Workers may run queued Cleanup and Transcode jobs to staging. Originals are not replaced in this mode.`;
    return;
  }
  if (mode === 'PlanAndReview') {
    el.hidden = false;
    el.innerHTML = `<strong>Safe planning mode.</strong> Probe and PlanReview jobs can run, but Cleanup/Transcode jobs will wait until Run staged work is enabled.`;
    return;
  }
  el.hidden = true;
  el.textContent = '';
}


function updateExecutionSettingsControls(status) {
  const cleanup = document.getElementById('auto-queue-cleanup-jobs');
  const transcode = document.getElementById('auto-queue-transcode-jobs');
  const requireReview = document.getElementById('require-review-before-auto-queue');
  const active = status.activeHours || {};
  if (cleanup) cleanup.checked = status.autoQueueCleanupJobs === true;
  if (transcode) transcode.checked = status.autoQueueTranscodeJobs === true;
  if (requireReview) requireReview.checked = status.requirePlanReviewBeforeAutoQueue !== false;
  setChecked('active-hours-enabled', active.enabled !== false);
  setValue('active-hours-time-zone', active.timeZoneId || 'Europe/London');
  setValue('active-hours-start', active.start || '08:30');
  setValue('active-hours-stop', active.stop || '02:00');
  setValue('active-hours-guard-minutes', active.stopNewWorkMinutesBefore ?? 30);
  const activeMessage = document.getElementById('active-hours-status');
  if (activeMessage) activeMessage.textContent = active.message || '';
}

function setChecked(id, value) {
  const el = document.getElementById(id);
  if (el) el.checked = value === true;
}

function setValue(id, value) {
  const el = document.getElementById(id);
  if (el) el.value = value;
}

function formatActiveHoursSummary(active) {
  if (!active || active.enabled === false) return 'Disabled';
  const state = active.allowStagedWork ? 'Open' : 'Paused';
  return `${state} · ${active.start || '08:30'}-${active.stop || '02:00'}`;
}

async function saveExecutionSettings() {
  const message = document.getElementById('execution-settings-message');
  try {
    const result = await api('/api/system/execution', {
      method: 'PUT',
      body: JSON.stringify({
        autoQueueCleanupJobs: document.getElementById('auto-queue-cleanup-jobs')?.checked === true,
        autoQueueTranscodeJobs: document.getElementById('auto-queue-transcode-jobs')?.checked === true,
        requirePlanReviewBeforeAutoQueue: document.getElementById('require-review-before-auto-queue')?.checked !== false,
        activeHoursEnabled: document.getElementById('active-hours-enabled')?.checked !== false,
        activeHoursTimeZoneId: document.getElementById('active-hours-time-zone')?.value || 'Europe/London',
        activeHoursStart: document.getElementById('active-hours-start')?.value || '08:30',
        activeHoursStop: document.getElementById('active-hours-stop')?.value || '02:00',
        stopNewWorkMinutesBefore: parseInt(document.getElementById('active-hours-guard-minutes')?.value || '30', 10) || 0
      })
    });
    const paused = result.activeHoursPaused ? ' Active hours are currently paused, so no staged jobs were queued.' : '';
    if (message) { message.textContent = `Automation settings saved. Queued ${result.queuedCleanup || 0} cleanup and ${result.queuedTranscode || 0} transcode job(s).${paused}`; message.className = 'form-message ok'; }
    await refreshStatus();
  } catch (error) {
    if (message) { message.textContent = `Save failed: ${error.message}`; message.className = 'form-message bad'; }
  }
}

function renderWorkerActivity(worker) {
  const activeJobs = worker.activeJobs || [];
  if (!activeJobs.length) {
    if (worker.controlState !== 'Normal') return `<span class="status ok">Idle · safe to stop</span>`;
    return `<span class="status">Idle</span>`;
  }

  return activeJobs.map(job => {
    const progress = job.progress == null ? '' : ` · ${Math.round(job.progress)}%`;
    const media = job.mediaItemId ? ` · media ${job.mediaItemId}` : '';
    const message = job.message ? `<small>${escapeHtml(job.message)}</small>` : '';
    return `<div class="worker-job">${badge(job.jobType)} <span>#${job.jobId}${media}${progress}</span>${message}</div>`;
  }).join('');
}


function renderWorkerMappings(worker) {
  const id = escapeAttribute(worker.workerId);
  const prefixes = Array.isArray(worker.serverPrefixes) ? worker.serverPrefixes : [];
  const summary = prefixes.length
    ? prefixes.slice(0, 3).map(x => `<div><small>${escapeHtml(x)}</small></div>`).join('') + (prefixes.length > 3 ? `<small>+${prefixes.length - 3} more</small>` : '')
    : '<small>No mappings reported</small>';
  return `${summary}<button class="button small" onclick="editWorkerMappings('${id}')">Edit</button>`;
}

window.editWorkerMappings = async (workerId) => {
  const current = await api(`/api/workers/${encodeURIComponent(workerId)}/runtime-settings`);
  const lines = (current.pathMappings || []).map(x => `${x.serverPrefix}=>${x.workerPrefix}`).join('\n');
  const value = prompt('Worker path mappings. One per line: serverPrefix=>workerPrefix\nThese are saved to the worker local runtime settings file while the worker is running.', lines);
  if (value == null) return;
  const pathMappings = value.split('\n')
    .map(x => x.trim())
    .filter(Boolean)
    .map(line => {
      const parts = line.split('=>');
      return { serverPrefix: (parts[0] || '').trim(), workerPrefix: (parts.slice(1).join('=>') || '').trim() };
    })
    .filter(x => x.serverPrefix && x.workerPrefix);
  await api(`/api/workers/${encodeURIComponent(workerId)}/runtime-settings`, { method: 'PUT', body: JSON.stringify({ pathMappings }) });
  alert('Worker mappings saved. The worker will apply them on its next poll and rerun path checks. Docker/container workers still need the paths mounted correctly.');
  await refreshWorkers();
};

function renderWorkerSafety(worker) {
  if (worker.activeJobCount > 0) return `<span class="status warn">Busy (${worker.activeJobCount})</span>`;
  if (worker.safeToStop) return `<span class="status ok">Safe to quit</span>`;
  return `<span class="status">Idle</span>`;
}

function renderWorkerActions(worker) {
  const id = escapeAttribute(worker.workerId);
  return `
    <div class="button-row">
      <button class="button" onclick="setWorkerControl('${id}', 'Normal')">Resume</button>
      <button class="button" onclick="setWorkerControl('${id}', 'Drain')">Drain</button>
      <button class="button" onclick="setWorkerControl('${id}', 'DrainThenExit')">Exit After Current</button>
      <button class="button" onclick="setWorkerControl('${id}', 'DrainThenShutdown')">Shutdown After Current</button>
    </div>`;
}

async function refreshWorkers() {
  const workers = await api('/api/workers');
  renderTable('workers-table', [
    { title: 'Worker', render: w => `<strong>${w.workerName}</strong><br><small>${w.workerId}</small>` },
    { title: 'State', render: w => badge(w.state) },
    { title: 'Control', render: w => badge(w.controlState) },
    { title: 'Activity', render: renderWorkerActivity },
    { title: 'Safe?', render: renderWorkerSafety },
    { title: 'Roles', key: 'roles' },
    { title: 'FFmpeg', render: w => w.capabilities?.ffmpeg?.version ?? (w.capabilities?.ffmpeg?.available ? 'available' : 'missing') },
    { title: 'Encoding', render: renderWorkerEncoding },
    { title: 'Local Work', render: w => w.localStorage?.localWorkingRootWritable ? badge('Writable') : badge('NotWritable') },
    { title: 'Last Seen', render: w => date(w.lastSeenUtc) },
    { title: 'Mappings', render: renderWorkerMappings },
    { title: 'Actions', render: renderWorkerActions }
  ], workers);
}


function renderWorkerEncoding(worker) {
  const caps = worker.capabilities || {};
  const cpuEnabled = caps.allowCpuEncoding !== false;
  const gpuEnabled = caps.allowGpuEncoding !== false;
  const cpu = (caps.cpuEncoders || []).slice(0, 4).join(', ');
  const gpu = (caps.gpuEncoders || []).slice(0, 4).join(', ');
  const cpuMore = (caps.cpuEncoders || []).length > 4 ? ` +${(caps.cpuEncoders || []).length - 4}` : '';
  const gpuMore = (caps.gpuEncoders || []).length > 4 ? ` +${(caps.gpuEncoders || []).length - 4}` : '';
  return `<div>${cpuEnabled ? badge('CPU') : badge('CPU off')} <small>${escapeHtml(cpu)}${cpuMore}</small></div>`
    + `<div>${gpuEnabled ? badge('GPU') : badge('GPU off')} <small>${escapeHtml(gpu)}${gpuMore}</small></div>`;
}

window.setWorkerControl = async (workerId, controlState) => {
  await api(`/api/workers/${encodeURIComponent(workerId)}/control`, { method: 'POST', body: JSON.stringify({ controlState, reason: 'UI action' }) });
  await refreshWorkers();
};

async function refreshDashboardLibraries() {
  if (!document.getElementById('dashboard-library-overview')) return;
  if (!state.libraries?.length) {
    try {
      state.libraries = await api('/api/libraries');
      state.librariesById = new Map(state.libraries.map(x => [x.id, x]));
    } catch {
      state.libraries = [];
    }
  }
  renderDashboardLibraryOverview(state.libraries || []);
}

function renderDashboardLibraryOverview(libraries) {
  const container = document.getElementById('dashboard-library-overview');
  if (!container) return;
  if (!libraries.length) {
    container.innerHTML = '<p class="muted">No libraries configured.</p>';
    return;
  }
  container.innerHTML = libraries.map(library => {
    const policy = library.policy || {};
    const execution = policy.execution || {};
    const video = policy.video || {};
    const audio = policy.audio || {};
    const canCleanup = execution.generateCleanupJobs !== false;
    const canTranscode = execution.generateTranscodeJobs === true;
    return `<div class="library-overview-card">
      <div class="library-overview-title">${escapeHtml(library.name)}</div>
      <div class="muted">${escapeHtml(library.rootPath || '')}</div>
      <div class="policy-pill-row">${badge(library.enabled ? 'Enabled' : 'Disabled')} ${badge(policy.processingStrategy || 'Strategy?')} ${canCleanup ? badge('Cleanup jobs') : ''} ${canTranscode ? badge('Transcode jobs') : ''}</div>
      <div class="library-overview-detail">Video: ${(video.onlyConvertCodecs || []).join(', ') || '?'} → ${escapeHtml(video.targetCodec || '?')} · ${escapeHtml(video.transcodeEngine || 'PreferGpu')}</div>
      <div class="library-overview-detail">Audio: ${escapeHtml(audio.premiumMode || 'Best premium + compatibility')} · ${escapeHtml(audio.premiumRanking || 'Atmos then TrueHD')}</div>
    </div>`;
  }).join('');
}

async function refreshLibraries() {
  const libraries = await api('/api/libraries');
  state.libraries = libraries;
  state.librariesById = new Map(libraries.map(x => [x.id, x]));
  updateMediaBrowserLibrarySelect();
  renderDashboardLibraryOverview(libraries);
  try { await loadIntegrations(); } catch { state.integrations = state.integrations || []; }
  let watchStatuses = [];
  try { watchStatuses = await api('/api/libraries/watch-status'); } catch { watchStatuses = []; }
  const watchByLibrary = new Map((watchStatuses || []).map(x => [x.libraryId, x]));
  renderTable('libraries-table', [
    { title: 'Library', render: renderLibrarySummary },
    { title: 'Execution', render: l => `<div class="library-cell-group"><div>${renderLibraryStrategyControls(l)}</div>${renderLibraryExecutionControls(l)}</div>` },
    { title: 'Video', render: renderLibraryVideoSummary },
    { title: 'Audio', render: renderLibraryAudioControls },
    { title: 'Metadata / Watch', render: l => `<div class="library-cell-group">${renderLibraryMetadataControls(l)}<div>${renderLibraryWatch(l, watchByLibrary.get(l.id))}</div><small>Updated ${escapeHtml(date(l.updatedUtc))}</small></div>` },
    { title: 'Actions', render: renderLibraryActions }
  ], libraries);
}

function renderLibrarySummary(library) {
  return `<div class="library-summary">
    <strong>${escapeHtml(library.name)}</strong> ${library.enabled ? badge('Enabled') : badge('Disabled')}
    <br><small>${escapeHtml(library.rootPath || '')}</small>
  </div>`;
}

function renderLibraryVideoSummary(library) {
  const video = library.policy?.video || {};
  const convert = (video.onlyConvertCodecs || []).join(', ') || '?';
  const target = video.targetCodec || '?';
  return `<div class="library-cell-group">
    <div><strong>${escapeHtml(convert)} → ${escapeHtml(target)}</strong></div>
    <div>${renderLibraryEngineControls(library)}</div>
    <small>${escapeHtml(video.profile || '')}</small>
  </div>`;
}

function renderLibraryActions(library) {
  return `<div class="button-row">
    <button class="button" onclick="saveLibraryMetadata(${library.id})">Save Policy</button>
    <button class="button" onclick="scanLibrary(${library.id})">Scan</button>
    <button class="button" onclick="refreshLibraryMetadata(${library.id})">Refresh Metadata</button>
    <button class="button" onclick="queueLibraryCleanup(${library.id})">Queue Cleanup</button>
    <button class="button" onclick="queueLibraryTranscode(${library.id})">Queue Transcode</button>
    <button class="button" onclick="replaceLibrary(${library.id})">Replace Staged</button>
  </div>`;
}

function renderLibraryStrategyControls(library) {
  const selected = library.policy?.processingStrategy || 'TranscodeOnly';
  const options = ['CleanupOnly', 'TranscodeOnly', 'CleanupThenTranscode']
    .map(value => `<option value="${value}" ${value === selected ? 'selected' : ''}>${value}</option>`)
    .join('');
  return `<select id="library-${library.id}-processing-strategy">${options}</select>`;
}


function renderLibraryExecutionControls(library) {
  const execution = library.policy?.execution || {};
  const output = library.policy?.output || {};
  return `<div class="checkbox-stack compact-controls">
    <label><input id="library-${library.id}-generate-cleanup-jobs" type="checkbox" ${execution.generateCleanupJobs !== false ? 'checked' : ''}> Generate cleanup</label>
    <label><input id="library-${library.id}-generate-transcode-jobs" type="checkbox" ${execution.generateTranscodeJobs === true ? 'checked' : ''}> Generate transcode</label>
    <label><input id="library-${library.id}-replace-originals" type="checkbox" ${output.replaceOriginals === true ? 'checked' : ''}> Allow replace</label>
  </div>`;
}


function renderLibraryEngineControls(library) {
  const selected = library.policy?.video?.transcodeEngine || 'PreferGpu';
  const labels = {
    PreferGpu: 'Prefer GPU',
    GpuOnly: 'GPU only',
    PreferCpu: 'Prefer CPU',
    CpuOnly: 'CPU only',
    Either: 'Either'
  };
  return `<select id="library-${library.id}-transcode-engine">${Object.keys(labels)
    .map(value => `<option value="${value}" ${value === selected ? 'selected' : ''}>${labels[value]}</option>`)
    .join('')}</select>`;
}

function renderLibraryAudioControls(library) {
  const audio = library.policy?.audio || {};
  const mode = audio.duplicateMode || 'KeepBestPerLanguage';
  const premiumMode = audio.premiumMode || 'KeepBestPremiumAndCompatibility';
  const ranking = audio.premiumRanking || 'PreferAtmosThenTrueHd';
  const preferred = audio.preferredChannels ?? 6;
  const maximum = audio.maximumChannels ?? 8;
  const modes = {
    KeepBestPerLanguage: 'Best/lang',
    KeepBestAndStereoPerLanguage: 'Best+stereo',
    KeepAllAllowed: 'All allowed'
  };
  const premiumModes = {
    KeepBestPremiumAndCompatibility: 'Best premium + compat',
    KeepBestPremiumOnly: 'Best premium only',
    KeepBestTwoPremiumAndCompatibility: 'Best two + compat',
    KeepAllPremium: 'All premium',
    KeepCompatibilityOnly: 'Compat only'
  };
  const rankings = {
    PreferAtmosThenTrueHd: 'Atmos→TrueHD',
    PreferAtmosThenDts: 'Atmos→DTS',
    PreferTrueHd: 'TrueHD first',
    PreferDts: 'DTS first'
  };
  return `<div class="metadata-controls compact-controls">
    <label><small>Duplicate</small><select id="library-${library.id}-audio-duplicate-mode">${Object.keys(modes).map(value => `<option value="${value}" ${value === mode ? 'selected' : ''}>${modes[value]}</option>`).join('')}</select></label>
    <label><small>Premium</small><select id="library-${library.id}-premium-audio-mode">${Object.keys(premiumModes).map(value => `<option value="${value}" ${value === premiumMode ? 'selected' : ''}>${premiumModes[value]}</option>`).join('')}</select></label>
    <label><small>Ranking</small><select id="library-${library.id}-premium-audio-ranking">${Object.keys(rankings).map(value => `<option value="${value}" ${value === ranking ? 'selected' : ''}>${rankings[value]}</option>`).join('')}</select></label>
    <label><small>Pref ch</small><input id="library-${library.id}-preferred-audio-channels" type="number" min="1" max="32" value="${escapeAttribute(String(preferred))}"></label>
    <label><small>Max ch</small><input id="library-${library.id}-maximum-audio-channels" type="number" min="0" max="32" value="${escapeAttribute(String(maximum))}"></label>
    <label class="inline-check"><input id="library-${library.id}-original-language-first" type="checkbox" ${audio.originalLanguageFirst !== false ? 'checked' : ''}> Original first</label>
    <label class="inline-check"><input id="library-${library.id}-preserve-spatial-audio" type="checkbox" ${audio.preserveSpatialAudio !== false ? 'checked' : ''}> Spatial</label>
    <label class="inline-check"><input id="library-${library.id}-preserve-lossless-audio" type="checkbox" ${audio.preserveLosslessAudio !== false ? 'checked' : ''}> Premium</label>
    <label class="inline-check"><input id="library-${library.id}-keep-compatibility-track" type="checkbox" ${audio.keepCompatibilityTrack !== false ? 'checked' : ''}> Compat</label>
    <label class="inline-check"><input id="library-${library.id}-review-different-mix-titles" type="checkbox" ${audio.reviewDifferentMixTitles !== false ? 'checked' : ''}> Mix review</label>
  </div>`;
}

function renderLibraryMetadataControls(library) {
  const metadata = library.policy?.metadata || {};
  const priority = (metadata.metadataSourcePriority || []).join(',');
  const radarrId = metadata.radarrIntegrationId ?? '';
  const sonarrId = metadata.sonarrIntegrationId ?? '';
  const useTmdb = metadata.useTmdb !== false;
  return `
    <div class="metadata-controls">
      <label><small>Priority</small><input id="library-${library.id}-metadata-priority" type="text" value="${escapeAttribute(priority || 'Radarr,Sonarr,Tmdb,FileProbe')}"></label>
      <label><small>Radarr</small><select id="library-${library.id}-radarr-integration-id">${integrationOptions('Radarr', radarrId)}</select></label>
      <label><small>Sonarr</small><select id="library-${library.id}-sonarr-integration-id">${integrationOptions('Sonarr', sonarrId)}</select></label>
      <label class="inline-check"><input id="library-${library.id}-use-tmdb" type="checkbox" ${useTmdb ? 'checked' : ''}> TMDb</label>
    </div>`;
}

window.saveLibraryMetadata = async (libraryId) => {
  const library = state.librariesById.get(libraryId);
  if (!library) return;
  const policy = library.policy || {};
  policy.processingStrategy = document.getElementById(`library-${libraryId}-processing-strategy`)?.value || policy.processingStrategy || 'TranscodeOnly';
  policy.execution = policy.execution || {};
  policy.execution.generateCleanupJobs = document.getElementById(`library-${libraryId}-generate-cleanup-jobs`)?.checked ?? true;
  policy.execution.generateTranscodeJobs = document.getElementById(`library-${libraryId}-generate-transcode-jobs`)?.checked ?? false;
  policy.output = policy.output || {};
  policy.output.stagingOnly = true;
  policy.output.mirrorFolderStructure = true;
  policy.output.replaceOriginals = document.getElementById(`library-${libraryId}-replace-originals`)?.checked ?? false;
  policy.video = policy.video || {};
  policy.video.transcodeEngine = document.getElementById(`library-${libraryId}-transcode-engine`)?.value || policy.video.transcodeEngine || 'PreferGpu';
  policy.audio = policy.audio || {};
  policy.audio.duplicateMode = document.getElementById(`library-${libraryId}-audio-duplicate-mode`)?.value || policy.audio.duplicateMode || 'KeepBestPerLanguage';
  policy.audio.premiumMode = document.getElementById(`library-${libraryId}-premium-audio-mode`)?.value || policy.audio.premiumMode || 'KeepBestPremiumAndCompatibility';
  policy.audio.premiumRanking = document.getElementById(`library-${libraryId}-premium-audio-ranking`)?.value || policy.audio.premiumRanking || 'PreferAtmosThenTrueHd';
  policy.audio.preferredChannels = numberOrDefault(document.getElementById(`library-${libraryId}-preferred-audio-channels`)?.value, policy.audio.preferredChannels ?? 6);
  policy.audio.maximumChannels = numberOrDefault(document.getElementById(`library-${libraryId}-maximum-audio-channels`)?.value, policy.audio.maximumChannels ?? 8);
  policy.audio.originalLanguageFirst = document.getElementById(`library-${libraryId}-original-language-first`)?.checked ?? true;
  policy.audio.preserveSpatialAudio = document.getElementById(`library-${libraryId}-preserve-spatial-audio`)?.checked ?? true;
  policy.audio.preserveLosslessAudio = document.getElementById(`library-${libraryId}-preserve-lossless-audio`)?.checked ?? true;
  policy.audio.keepCompatibilityTrack = document.getElementById(`library-${libraryId}-keep-compatibility-track`)?.checked ?? true;
  policy.audio.reviewDifferentMixTitles = document.getElementById(`library-${libraryId}-review-different-mix-titles`)?.checked ?? true;
  policy.metadata = policy.metadata || {};
  policy.metadata.metadataSourcePriority = csv(document.getElementById(`library-${libraryId}-metadata-priority`)?.value || 'Radarr,Sonarr,Tmdb,FileProbe');
  policy.metadata.radarrIntegrationId = optionalInt(document.getElementById(`library-${libraryId}-radarr-integration-id`)?.value || '');
  policy.metadata.sonarrIntegrationId = optionalInt(document.getElementById(`library-${libraryId}-sonarr-integration-id`)?.value || '');
  policy.metadata.useTmdb = document.getElementById(`library-${libraryId}-use-tmdb`)?.checked ?? true;
  policy.metadata.matchByPath = true;

  await api(`/api/libraries/${libraryId}`, {
    method: 'PUT',
    body: JSON.stringify({
      name: library.name,
      rootPath: library.rootPath,
      enabled: library.enabled,
      policy
    })
  });
  state.formDirty = false;
  await refreshLibraries();
};

function renderLibraryWatch(library, watch) {
  if (!library.policy?.watch?.enabled) return badge('Disabled');
  const active = watch?.watcherActive ? badge('Active') : badge('Waiting');
  const pending = watch?.pendingFileCount ? `<br><small>${watch.pendingFileCount} pending</small>` : '';
  const next = watch?.nextPeriodicScanUtc ? `<br><small>Next rescan: ${date(watch.nextPeriodicScanUtc)}</small>` : '';
  const error = watch?.lastError ? `<br><small class="text-bad">${escapeHtml(watch.lastError)}</small>` : '';
  return `${active}${pending}${next}${error}`;
}

window.scanLibrary = async (libraryId) => {
  await api(`/api/libraries/${libraryId}/scan`, { method: 'POST', body: JSON.stringify({ force: false, createProbeJobs: true }) });
  await refreshLibraries();
};


window.queueLibraryCleanup = async (libraryId) => {
  if (!confirm('Queue Cleanup jobs for every approved cleanup plan in this library? Originals are untouched; outputs go to staging.')) return;
  try {
    const result = await api(`/api/libraries/${libraryId}/cleanup/queue`, { method: 'POST' });
    alert(result.messages?.join('\n') || `Queued ${result.queued || 0} cleanup job(s).`);
    await refreshMedia();
    await refreshJobs();
  } catch (error) {
    alert(error.message || error);
  }
};

window.queueLibraryTranscode = async (libraryId) => {
  if (!confirm('Queue Transcode jobs for every approved transcode plan in this library? Originals are untouched; outputs go to staging.')) return;
  try {
    const result = await api(`/api/libraries/${libraryId}/transcode/queue`, { method: 'POST' });
    alert(result.messages?.join('\n') || `Queued ${result.queued || 0} transcode job(s).`);
    await refreshMedia();
    await refreshJobs();
  } catch (error) {
    alert(error.message || error);
  }
};

window.replaceLibrary = async (libraryId) => {
  if (!confirm('Replace every staged/approved output in this library? Originals are moved to quarantine, and the staged file takes the same original filename.')) return;
  try {
    const result = await api(`/api/libraries/${libraryId}/replace`, { method: 'POST' });
    alert(result.messages?.join('\n') || `Replaced ${result.replaced || 0} item(s).`);
    await refreshLibraries();
    await refreshMedia();
  } catch (error) {
    alert(error.message || error);
  }
};

function buildJobQuery(page, pageSize, filters = {}) {
  const params = new URLSearchParams();
  params.set('page', String(page));
  params.set('pageSize', String(pageSize));
  if (filters.jobType) params.set('jobType', filters.jobType);
  if (filters.status) params.set('status', filters.status);
  if (filters.workerId) params.set('workerId', filters.workerId);
  if (filters.activeOnly) params.set('activeOnly', 'true');
  return params.toString();
}

function applyJobFilterControls() {
  const type = document.getElementById('job-filter-type');
  const status = document.getElementById('job-filter-status');
  const worker = document.getElementById('job-filter-worker');
  const activeOnly = document.getElementById('job-filter-active-only');
  if (type) type.value = state.jobFilters.jobType || '';
  if (status) status.value = state.jobFilters.status || '';
  if (worker) worker.value = state.jobFilters.workerId || '';
  if (activeOnly) activeOnly.checked = state.jobFilters.activeOnly === true;
}

function applyJobFilters() {
  state.jobFilters = {
    jobType: document.getElementById('job-filter-type')?.value || '',
    status: document.getElementById('job-filter-status')?.value || '',
    workerId: document.getElementById('job-filter-worker')?.value.trim() || '',
    activeOnly: document.getElementById('job-filter-active-only')?.checked === true
  };
  state.paging.jobs.page = 1;
  state.formDirty = false;
  refreshJobs();
}

function resetJobFilters() {
  state.jobFilters = { jobType: '', status: '', workerId: '', activeOnly: false };
  state.paging.jobs.page = 1;
  state.formDirty = false;
  applyJobFilterControls();
  refreshJobs();
}

async function clearFinishedJobs() {
  if (!confirm('Remove completed/cancelled/expired jobs from the jobs list? Failed jobs are kept.')) return;
  const result = await api('/api/jobs/finished', { method: 'DELETE' });
  alert(`Removed ${result.deleted || 0} finished job(s).`);
  await refreshJobs();
}

async function refreshJobs() {
  const pager = state.paging.jobs;
  applyJobFilterControls();

  if (document.getElementById('jobs-table')) {
    const result = await api(`/api/jobs?${buildJobQuery(pager.page, pager.pageSize, state.jobFilters)}`);
    updatePager('jobs', result);
    renderTable('jobs-table', jobColumns(), result.items ?? []);
  }

  if (document.getElementById('dashboard-active-jobs-table')) {
    const active = await api('/api/jobs?page=1&pageSize=12&activeOnly=true');
    renderTable('dashboard-active-jobs-table', jobColumns({ compact: true }), active.items ?? []);
  }

  if (document.getElementById('dashboard-finished-jobs-table')) {
    const finished = await api('/api/jobs?page=1&pageSize=10&status=Completed');
    renderTable('dashboard-finished-jobs-table', jobColumns({ compact: true, finished: true }), finished.items ?? []);
  }
}

function jobColumns(options = {}) {
  const compact = !!options.compact;
  const columns = [
    { title: 'ID', key: 'id' },
    { title: 'Type', key: 'jobType' },
    { title: 'Status', render: j => badge(j.status) },
    { title: 'Library', render: j => j.libraryName ? escapeHtml(j.libraryName) : `<small>${j.libraryId ?? ''}</small>` },
    { title: 'Media', render: renderJobMedia },
    { title: 'Worker', key: 'leasedByWorkerId' },
    { title: 'Required', render: renderJobRequirement },
    { title: 'Attempt', render: j => `${j.attemptNumber}/${j.maxAttempts}` },
    { title: 'Progress', render: j => j.progress == null ? '' : `${Math.round(j.progress)}%` },
    { title: 'Error', key: 'lastError' },
    { title: 'Created', render: j => date(j.createdUtc) }
  ];

  if (compact) {
    return columns.filter(c => !['Required', 'Attempt', 'Error'].includes(c.title));
  }

  return columns;
}


function renderJobRequirement(job) {
  if (!job.requiredEncoder && (!job.requiredEncoderEngine || job.requiredEncoderEngine === 'Unknown')) return '';
  const engine = job.requiredEncoderEngine && job.requiredEncoderEngine !== 'Unknown' ? badge(job.requiredEncoderEngine) : '';
  const encoder = job.requiredEncoder ? `<br><small>${escapeHtml(job.requiredEncoder)}</small>` : '';
  return `${engine}${encoder}`;
}

function renderJobMedia(job) {
  if (!job.mediaItemId) return '';
  const name = job.mediaName || job.mediaRelativePath || `Media ${job.mediaItemId}`;
  const path = job.mediaRelativePath && job.mediaRelativePath !== name
    ? `<br><small>${escapeHtml(job.mediaRelativePath)}</small>`
    : '';
  return `<strong>${escapeHtml(name)}</strong><br><small>ID ${job.mediaItemId}</small>${path}`;
}

function updateMediaBrowserLibrarySelect() {
  const select = document.getElementById('media-browser-library');
  if (!select) return;
  const current = state.mediaBrowser.libraryId == null ? '' : String(state.mediaBrowser.libraryId);
  select.innerHTML = `<option value="">Select library</option>` + (state.libraries || [])
    .map(l => `<option value="${l.id}" ${String(l.id) === current ? 'selected' : ''}>${escapeHtml(l.name)}</option>`)
    .join('');
  if (!state.mediaBrowser.libraryId && state.libraries?.length) {
    state.mediaBrowser.libraryId = state.libraries[0].id;
    select.value = String(state.mediaBrowser.libraryId);
  }
}

function renderBrowserTotals(totals) {
  if (!totals) return '';
  const actual = totals.actualSavedBytes ? formatBytes(totals.actualSavedBytes) : '0 B';
  const estimated = `${formatBytes(totals.estimatedCleanupSavingsBytes || 0)}${totals.estimatedCleanupSavingsComplete ? '' : ' + unknown'}`;
  return `<div class="card-grid browser-card-grid">
    <div class="card"><div class="card-title">Items</div><div class="card-value">${totals.itemCount || 0}</div></div>
    <div class="card"><div class="card-title">Original Size</div><div class="card-value">${formatBytes(totals.originalSizeBytes || 0)}</div></div>
    <div class="card"><div class="card-title">Actual Saved</div><div class="card-value">${actual}</div><small>Cleanup ${formatBytes(totals.cleanupSavedBytes || 0)} · Transcode ${formatBytes(totals.transcodeSavedBytes || 0)}</small></div>
    <div class="card"><div class="card-title">Estimated Cleanup Save</div><div class="card-value">${estimated}</div></div>
  </div>`;
}

async function refreshMediaBrowser() {
  if (state.page !== 'media') return;
  if (!state.libraries?.length) {
    try { await refreshLibraries(); } catch { }
  }
  updateMediaBrowserLibrarySelect();
  const libraryId = state.mediaBrowser.libraryId;
  const table = document.getElementById('media-browser-table');
  if (!libraryId || !table) return;
  const path = encodeURIComponent(state.mediaBrowser.path || '');
  const browser = await api(`/api/media/browser?libraryId=${libraryId}&path=${path}`);
  state.mediaBrowser.path = browser.currentPath || '';
  const crumbs = document.getElementById('media-browser-breadcrumbs');
  if (crumbs) {
    crumbs.innerHTML = (browser.breadcrumbs || []).map(c => `<button class="link-button" onclick="openMediaBrowserPath('${escapeAttribute(c.path)}')">${escapeHtml(c.name)}</button>`).join(' / ');
  }
  const totals = document.getElementById('media-browser-totals');
  if (totals) totals.innerHTML = renderBrowserTotals(browser.totals);
  const rows = [
    ...(browser.directories || []).map(d => ({ kind: 'Folder', name: d.name, path: d.path, totals: d.totals })),
    ...(browser.items || []).map(i => ({ kind: 'File', name: i.relativePath.split(/[\\/]/).pop(), media: i, totals: null }))
  ];
  renderTable('media-browser-table', [
    { title: 'Type', render: r => r.kind === 'Folder' ? badge('Folder') : badge('File') },
    { title: 'Name', render: r => r.kind === 'Folder' ? `<button class="link-button" onclick="openMediaBrowserPath('${escapeAttribute(r.path)}')">${escapeHtml(r.name)}</button>` : escapeHtml(r.name) },
    { title: 'Items', render: r => r.totals?.itemCount ?? '' },
    { title: 'Size', render: r => r.totals ? formatBytes(r.totals.originalSizeBytes || 0) : formatBytes(r.media?.fileSizeBytes || 0) },
    { title: 'Actual Saved', render: r => r.totals ? formatBytes(r.totals.actualSavedBytes || 0) : renderActualSaving(r.media) },
    { title: 'Est. Save', render: r => r.totals ? `${formatBytes(r.totals.estimatedCleanupSavingsBytes || 0)}${r.totals.estimatedCleanupSavingsComplete ? '' : ' + unknown'}` : renderCleanupEstimate(r.media) },
    { title: 'Status', render: r => r.media ? badge(r.media.status) : '' },
    { title: 'Actions', render: r => r.media ? renderMediaActions(r.media) : '' }
  ], rows);
}

window.openMediaBrowserPath = async (path) => {
  state.mediaBrowser.path = path || '';
  await refreshMediaBrowser();
};

async function refreshMedia() {
  const pager = state.paging.media;
  const result = await api(`/api/media?page=${pager.page}&pageSize=${pager.pageSize}`);
  updatePager('media', result);
  renderTable('media-table', [
    { title: 'ID', key: 'id' },
    { title: 'Status', render: m => badge(m.status) },
    { title: 'Path', key: 'relativePath' },
    { title: 'Size', render: m => formatBytes(m.fileSizeBytes ?? 0) },
    { title: 'Actual Saved', render: renderActualSaving },
    { title: 'Est. Cleanup Save', render: renderCleanupEstimate },
    { title: 'Probe', render: m => m.hasProbe ? badge('Probed') : badge('NoProbe') },
    { title: 'Plan', render: m => m.hasPlan ? badge('Planned') : badge('NoPlan') },
    { title: 'Original Lang', render: renderMediaLanguage },
    { title: 'Modified', render: m => date(m.lastModifiedUtc) },
    { title: 'Actions', render: renderMediaActions }
  ], result.items ?? []);
}


function renderActualSaving(media) {
  const total = media.actualTotalSavedBytes ?? media.actualSavedBytes;
  const cleanup = media.actualCleanupSavedBytes;
  const transcode = media.actualTranscodeSavedBytes;
  if (total === null || total === undefined) return '';
  const parts = [];
  if (cleanup !== null && cleanup !== undefined) parts.push(`Cleanup ${formatBytes(cleanup)}`);
  if (transcode !== null && transcode !== undefined) parts.push(`Transcode ${formatBytes(transcode)}`);
  const kind = media.lastCompletedWorkType ? `<br><small>${escapeHtml(media.lastCompletedWorkType)} · ${escapeHtml(date(media.lastWorkCompletedUtc))}</small>` : '';
  const breakdown = parts.length ? `<br><small>${escapeHtml(parts.join(' · '))}</small>` : '';
  const marker = media.replacedOriginal
    ? `<br><small>replaced · quarantine kept</small>`
    : media.stagingTransferComplete ? '<br><small>staging complete</small>' : '';
  return `<strong>Total ${formatBytes(total)}</strong>${breakdown}${kind}${marker}`;
}

function renderCleanupEstimate(media) {
  if (!media.hasPlan || !media.planKind) return '';
  if (media.planKind === 'NoAction') return `<small>No cleanup needed</small>`;
  if (media.planKind !== 'CleanupOnly') return `<small>${escapeHtml(media.planKind)}</small>`;
  const save = formatBytes(media.estimatedCleanupSavingsBytes ?? 0);
  const suffix = media.estimatedCleanupSavingsComplete ? '' : ' + unknown';
  const output = media.estimatedCleanupOutputBytes != null ? `<br><small>Output est. ${formatBytes(media.estimatedCleanupOutputBytes)}</small>` : '';
  return `<strong>${save}${suffix}</strong>${output}`;
}

function renderMediaLanguage(media) {
  if (media.originalLanguage) {
    const refreshed = media.metadataRefreshedUtc ? ` · ${escapeHtml(date(media.metadataRefreshedUtc))}` : '';
    const source = media.originalLanguageSource ? escapeHtml(media.originalLanguageSource) : 'Unknown source';
    return `<strong>${escapeHtml(formatLanguage(media.originalLanguage))}</strong><br><small>${source}${refreshed}</small>`;
  }

  if (media.metadataError) {
    return `<span class="status warn">Unknown</span><br><small class="text-bad">${escapeHtml(media.metadataError)}</small>`;
  }

  return '<span class="status warn">Unknown</span>';
}

function formatLanguage(language) {
  const value = String(language || '').toLowerCase();
  const known = {
    eng: 'English', en: 'English',
    jpn: 'Japanese', ja: 'Japanese',
    deu: 'German', ger: 'German', de: 'German',
    fra: 'French', fre: 'French', fr: 'French',
    spa: 'Spanish', es: 'Spanish',
    ita: 'Italian', it: 'Italian',
    kor: 'Korean', ko: 'Korean',
    zho: 'Chinese', chi: 'Chinese', zh: 'Chinese',
    por: 'Portuguese', pt: 'Portuguese',
    nld: 'Dutch', dut: 'Dutch', nl: 'Dutch',
    swe: 'Swedish', sv: 'Swedish',
    nor: 'Norwegian', no: 'Norwegian',
    dan: 'Danish', da: 'Danish',
    fin: 'Finnish', fi: 'Finnish',
    pol: 'Polish', pl: 'Polish',
    rus: 'Russian', ru: 'Russian',
    tur: 'Turkish', tr: 'Turkish',
    vie: 'Vietnamese', vi: 'Vietnamese',
    isl: 'Icelandic', ice: 'Icelandic', is: 'Icelandic'
  };

  return known[value] ? `${known[value]} (${language})` : language;
}

function renderMediaActions(media) {
  const queueButton = !media.hasPlan || media.planKind === 'NoAction'
    ? ''
    : media.planKind === 'CleanupOnly'
      ? `<button class="button" onclick="queueMediaCleanup(${media.id})">Queue Cleanup</button>`
      : `<button class="button" onclick="queueMediaTranscode(${media.id})">Queue Transcode</button>`;

  const replaceButton = ['StagedCleaned', 'Staged', 'Approved'].includes(media.status)
    ? `<button class="button" onclick="replaceMedia(${media.id})">Replace</button>`
    : '';
  return `<div class="button-row">
    <button class="button" onclick="refreshMediaMetadata(${media.id})">Metadata</button>
    <button class="button" onclick="queueMediaPlan(${media.id})">Plan</button>
    <button class="button" onclick="showMediaPlan(${media.id})">View Plan</button>
    ${media.hasPlan ? queueButton : ''}
    ${replaceButton}
  </div>`;
}


window.replaceMedia = async (mediaId) => {
  if (!confirm('Replace the original with this staged output? The original is moved to quarantine first.')) return;
  try {
    const result = await api(`/api/media/${mediaId}/replace`, { method: 'POST' });
    alert(result.message || 'Original replaced.');
    await refreshMedia();
    await refreshMediaBrowser();
  } catch (error) {
    alert(error.message || error);
  }
};

window.showMediaPlan = async (mediaId, title = null, subtitle = null) => {
  openPlanModal(title || `Media ${mediaId} Plan`, subtitle || 'Loading plan...');

  const content = document.getElementById('plan-modal-content');
  if (!content) return;

  content.innerHTML = `<div class="muted">Loading plan...</div>`;

  try {
    const plan = await api(`/api/media/${mediaId}/plan`);
    content.innerHTML = renderPlanDetails(plan);

    const modalSubtitle = document.getElementById('plan-modal-subtitle');
    if (modalSubtitle && !subtitle) {
      modalSubtitle.textContent = plan.inputPath || `Media ID ${mediaId}`;
    }
  } catch (error) {
    content.innerHTML = `<div class="form-message bad">No plan found for media ${mediaId}. Run Plan first.</div>`;
  }
};

function openPlanModal(title, subtitle = '') {
  const modal = document.getElementById('plan-modal');
  const modalTitle = document.getElementById('plan-modal-title');
  const modalSubtitle = document.getElementById('plan-modal-subtitle');

  if (!modal) return;
  if (modalTitle) modalTitle.textContent = title || 'Media Plan';
  if (modalSubtitle) modalSubtitle.textContent = subtitle || '';

  modal.hidden = false;
  document.body.classList.add('modal-open');
  document.getElementById('close-plan-modal')?.focus();
}

function closePlanModal() {
  const modal = document.getElementById('plan-modal');
  if (!modal || modal.hidden) return;

  modal.hidden = true;
  document.body.classList.remove('modal-open');
}

function renderPlanDetails(plan) {
  const ffmpegCommand = ['ffmpeg', ...(plan.ffmpegArgs || [])]
    .map(arg => arg.includes(' ') ? `"${arg}"` : arg)
    .join(' ');

  const warnings = (plan.warnings || []).length
    ? `<div class="plan-box"><strong>Warnings</strong><ul>${plan.warnings.map(w => `<li>${escapeHtml(w)}</li>`).join('')}</ul></div>`
    : '';
  const blocking = (plan.blockingReasons || []).length
    ? `<div class="plan-box"><strong>Blocking</strong><ul>${plan.blockingReasons.map(w => `<li>${escapeHtml(w)}</li>`).join('')}</ul></div>`
    : '';
  const savingsNotes = (plan.savingsNotes || []).length
    ? `<div class="plan-box"><strong>Savings notes</strong><ul>${plan.savingsNotes.map(w => `<li>${escapeHtml(w)}</li>`).join('')}</ul></div>`
    : '';
  const cleanupReasons = (plan.cleanupReasons || []).length
    ? `<div class="plan-box"><strong>Cleanup actions</strong><ul>${plan.cleanupReasons.map(w => `<li>${escapeHtml(w)}</li>`).join('')}</ul></div>`
    : plan.planKind === 'NoAction' ? `<div class="plan-box"><strong>Cleanup actions</strong><br>No cleanup/remux work required.</div>` : '';

  const removedBytes = plan.estimatedRemovedBytes ?? 0;
  const unknownSuffix = plan.estimatedSavingsComplete ? '' : ' + unknown';
  const savingsSummary = plan.planKind === 'CleanupOnly'
    ? `<div class="plan-box"><strong>Estimated cleanup saving</strong><br>${formatBytes(removedBytes)}${unknownSuffix}<br><small>Estimated output: ${formatBytes(plan.estimatedOutputSizeBytes ?? plan.inputFileSizeBytes ?? 0)}</small></div>`
    : `<div class="plan-box"><strong>Estimated stream removal saving</strong><br>${formatBytes(removedBytes)}${unknownSuffix}</div>`;

  const streams = (plan.streams || []).map(s => {
    const details = [s.codecName, s.language, s.channels ? `${s.channels}ch` : '', s.channelLayout, s.title].filter(Boolean).join(' · ');
    const flags = [s.protectedAudio ? 'premium' : '', s.compatibilityAudio ? 'compat' : '', s.outputDefault ? 'default' : ''].filter(Boolean).join(' · ');
    const output = s.outputStreamIndex == null ? '' : `out #${s.outputStreamIndex}${s.outputTypeIndex == null ? '' : ` / ${s.streamType}:${s.outputTypeIndex}`}`;
    return `
    <div class="stream-plan">
      <div>#${s.sourceStreamIndex}<br><small>${escapeHtml(output)}</small></div>
      <div>${escapeHtml(s.streamType || '')}</div>
      <div>${escapeHtml(details)}${flags ? `<br><small>${escapeHtml(flags)}</small>` : ''}</div>
      <div>${badge(s.action || '')}</div>
      <div>${s.estimatedSizeBytes == null ? '<small>Unknown</small>' : escapeHtml(formatBytes(s.estimatedSizeBytes))}<br><small>${escapeHtml(s.sizeEstimateSource || '')}</small></div>
      <div>${s.action === 'Remove' ? escapeHtml(formatBytes(s.estimatedSavingBytes ?? 0)) : ''}</div>
      <div>${escapeHtml(s.reason || '')}</div>
    </div>`;
  }).join('');

  return `
    <div class="plan-grid">
      <div class="plan-box"><strong>Media</strong><br>ID ${plan.mediaId}<br><small>${escapeHtml(plan.inputPath || '')}</small></div>
      <div class="plan-box"><strong>Plan</strong><br>${escapeHtml(plan.planKind || 'Transcode')}<br><small>${escapeHtml(plan.processingStrategy || '')}</small></div>
      <div class="plan-box"><strong>Target</strong><br>${escapeHtml(plan.targetVideoCodec || '')}<br><small>${escapeHtml(plan.videoProfile || '')}</small></div>
      <div class="plan-box"><strong>Engine policy</strong><br>${escapeHtml(plan.transcodeEnginePolicy || 'PreferGpu')}</div>
      <div class="plan-box"><strong>Encoder</strong><br>${escapeHtml(plan.requiredEncoder || 'none / copy')}<br><small>${escapeHtml(plan.requiredEncoderEngine || '')}</small></div>
      <div class="plan-box"><strong>Selection reason</strong><br><small>${escapeHtml(plan.encoderSelectionReason || '')}</small></div>
      <div class="plan-box"><strong>Input size</strong><br>${formatBytes(plan.inputFileSizeBytes ?? 0)}</div>
      ${savingsSummary}
      <div class="plan-box"><strong>Plan hash</strong><br><small>${escapeHtml(plan.planHash || '')}</small></div>
    </div>
    ${warnings}
    ${blocking}
    ${savingsNotes}
    <h3>Stream mapping</h3>
    <div class="stream-plan stream-plan-header">
      <div>Input / output</div><div>Type</div><div>Details</div><div>Action</div><div>Size estimate</div><div>Saving</div><div>Reason</div>
    </div>
    <div>${streams || '<p>No stream actions.</p>'}</div>
    <h3>Execution</h3>
    <div class="mode-warning"><strong>Plan only.</strong> v28 can auto-create cleanup/transcode jobs when server automation and the library generation settings allow it. Workers still run staging work only when Processing Mode allows it.</div>
    <h3>FFmpeg arguments</h3>
    <pre class="code-block">${escapeHtml(ffmpegCommand)}</pre>
    <h3>Staging output</h3>
    <pre class="code-block">${escapeHtml(plan.stagingOutputPath || '')}</pre>
  `;
}

window.queueMediaPlan = async (mediaId) => {
  await api(`/api/media/${mediaId}/plan?force=true`, { method: 'POST' });
  await refreshMedia();
  await refreshJobs();
  await refreshReview();
};

async function queueMediaWork(mediaId, kind) {
  try {
    const result = await api(`/api/media/${mediaId}/${kind}`, { method: 'POST' });
    if (result?.message) alert(result.message);
    await refreshMedia();
    await refreshJobs();
  } catch (error) {
    alert(error.message || error);
  }
}

window.queueMediaCleanup = async (mediaId) => {
  await queueMediaWork(mediaId, 'cleanup');
};

window.queueMediaTranscode = async (mediaId) => {
  await queueMediaWork(mediaId, 'transcode');
};

async function refreshReview() {
  const pager = state.paging.review;
  const result = await api(`/api/review?page=${pager.page}&pageSize=${pager.pageSize}`);
  updatePager('review', result);
  renderTable('review-table', [
    { title: 'ID', key: 'id' },
    { title: 'Library', render: r => r.libraryName ? escapeHtml(r.libraryName) : `<small>${r.libraryId ?? ''}</small>` },
    { title: 'Media', render: renderReviewMedia },
    { title: 'Type', key: 'reviewType' },
    { title: 'Severity', render: r => badge(r.severity) },
    { title: 'Reason', key: 'reason' },
    { title: 'Created', render: r => date(r.createdUtc) },
    { title: 'Actions', render: renderReviewActions }
  ], result.items ?? []);
}


function renderReviewMedia(review) {
  const name = review.mediaName || review.mediaRelativePath || `Media ${review.mediaItemId}`;
  const path = review.mediaRelativePath && review.mediaRelativePath !== name ? `<br><small>${escapeHtml(review.mediaRelativePath)}</small>` : '';
  return `<strong>${escapeHtml(name)}</strong><br><small>ID ${review.mediaItemId}</small>${path}`;
}

function renderReviewActions(review) {
  const name = review.mediaName || review.mediaRelativePath || `Media ${review.mediaItemId}`;
  const subtitle = [
    review.libraryName,
    review.mediaRelativePath
  ].filter(Boolean).join(' · ');

  return `
    <div class="button-row">
      <button class="button" onclick="showMediaPlan(${review.mediaItemId}, '${escapeAttribute(name)} Plan', '${escapeAttribute(subtitle)}')">View Plan</button>
      <button class="button primary" onclick="approveReview(${review.id})">Approve</button>
      <button class="button" onclick="skipReview(${review.id})">Skip</button>
    </div>`;
}

window.approveReview = async (reviewId) => {
  await api(`/api/review/${reviewId}/approve`, { method: 'POST' });
  await refreshReview();
  await refreshMedia();
};

window.skipReview = async (reviewId) => {
  await api(`/api/review/${reviewId}/skip`, { method: 'POST' });
  await refreshReview();
  await refreshMedia();
};

async function refreshAll(options = {}) {
  try {
    const force = options.force === true;
    const editing = isUserEditingPage() && !force;
    document.getElementById('connection-state').textContent = editing ? 'Paused while editing' : 'Polling';
    await refreshStatus({ updateControls: !editing || force });

    if (editing) {
      document.getElementById('connection-state').textContent = 'Live · editing paused';
      return;
    }

    if (state.page === 'dashboard' || state.page === 'jobs') await refreshJobs();
    if (state.page === 'dashboard') await refreshDashboardLibraries();
    if (state.page === 'workers') await refreshWorkers();
    if (state.page === 'libraries') await refreshLibraries();
    if (state.page === 'media') { await refreshMedia(); await refreshMediaBrowser(); }
    if (state.page === 'review') await refreshReview();
    if (state.page === 'integrations') await refreshIntegrations();
    document.getElementById('connection-state').textContent = 'Live by polling';
  } catch (error) {
    console.error(error);
    document.getElementById('connection-state').textContent = error.message?.includes('401') ? 'Unauthorized' : 'Disconnected';
  }
}

defaultLibraryFormValues();
setPage(state.page);
state.timer = setInterval(refreshAll, state.refreshMs);
