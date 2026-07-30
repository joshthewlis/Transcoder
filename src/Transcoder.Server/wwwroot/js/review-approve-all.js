(() => {
  const id = 'review-approve-all-tools';

  function isReviewLikelyVisible() {
    const text = (document.body?.innerText || '').toLowerCase();
    return text.includes('review') || location.hash.toLowerCase().includes('review');
  }

  function ensureTools() {
    if (document.getElementById(id)) return;
    if (!document.body || !isReviewLikelyVisible()) return;

    const box = document.createElement('div');
    box.id = id;
    box.className = 'toolbar compact review-approve-all-tools';
    box.style.display = 'flex';
    box.style.gap = '8px';
    box.style.alignItems = 'center';
    box.style.margin = '8px 0';
    box.innerHTML = `
      <button type="button" class="btn btn-secondary" id="repair-review-limbo-btn">Repair Missing Reviews</button>
      <button type="button" class="btn btn-warning" id="approve-all-reviews-btn">Approve All Reviews</button>
      <span id="review-approve-all-message" class="muted"></span>
    `;

    const heading = [...document.querySelectorAll('h1,h2,h3,.section-title,.card-title')]
      .find(x => (x.textContent || '').toLowerCase().includes('review'));
    const target = heading?.parentElement || document.querySelector('main') || document.body;
    target.insertBefore(box, heading?.nextSibling || target.firstChild);

    document.getElementById('repair-review-limbo-btn')?.addEventListener('click', repairLimbo);
    document.getElementById('approve-all-reviews-btn')?.addEventListener('click', approveAll);
  }

  async function postJson(url) {
    const response = await fetch(url, { method: 'POST' });
    const text = await response.text();
    let payload = null;
    try { payload = text ? JSON.parse(text) : null; } catch { payload = { message: text }; }
    if (!response.ok) throw new Error(payload?.message || text || response.statusText);
    return payload;
  }

  function setMessage(message) {
    const el = document.getElementById('review-approve-all-message');
    if (el) el.textContent = message || '';
  }

  async function repairLimbo() {
    try {
      setMessage('Repairing hidden review limbo...');
      const result = await postJson('/api/review/repair-limbo');
      setMessage(result?.message || 'Repair complete.');
      setTimeout(() => location.reload(), 900);
    } catch (error) {
      alert(error.message || error);
      setMessage('Repair failed.');
    }
  }

  async function approveAll() {
    if (!confirm('Approve every visible review item and every hidden/limbo plan review?')) return;
    try {
      setMessage('Approving all reviews...');
      const result = await postJson('/api/review/approve-all?includeLimbo=true');
      setMessage(result?.message || 'Approve all complete.');
      setTimeout(() => location.reload(), 900);
    } catch (error) {
      alert(error.message || error);
      setMessage('Approve all failed.');
    }
  }

  document.addEventListener('DOMContentLoaded', ensureTools);
  new MutationObserver(ensureTools).observe(document.documentElement, { childList: true, subtree: true });
  ensureTools();
})();
