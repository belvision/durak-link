const assert = require('node:assert/strict');
const { DurakSession } = require('./agent-session.cjs');

(async () => {
  const fake = Object.create(DurakSession.prototype), sent = [];
  let starts = 0;
  fake.mouse = { request: async command => { sent.push(command); return { delivered: true, elapsedMs: 10 }; } };
  fake.startHand = async () => { starts++; return { Decision: { Kind: 'attack', Template: 'FIRST_LOW_NONTRUMP', RequiresReview: false, Card: '8D' } }; };
  const hand = { Cards: ['KH', 'JH', 'JS', '9H', '8D', '6C'] };
  const frame = () => ({ calibrated: true, observedAt: Date.now(), cursor: { x: 90, y: 1025 }, targetY: 600,
    moveScale: 5.5, dragScale: 3.8, cards: hand.Cards.map((card, i) => ({ card, x: 45 + 80 * i, y: 923 })) });
  for (const name of ['open', 'first', 'ready', 'take', 'done']) await fake.button(name);
  assert.deepEqual(sent.splice(0), ['lobby-open', 'lobby-first', 'lobby-ready', 'table-take', 'table-done']);
  await assert.rejects(() => fake.button('random'));
  const result = await fake.firstMove(hand, frame());
  assert.equal(result.sent, true); assert.equal(starts, 1);
  assert.deepEqual(sent.splice(0), ['play 50 -19 0 -85']); // No lateral drag after grabbing.
  assert.equal(result.confirmed, undefined); // Only observation may confirm a played card.
  await assert.rejects(() => fake.firstMove(hand, { ...frame(), observedAt: Date.now() - 10000 }));
  await assert.rejects(() => fake.firstMove(hand, { ...frame(), calibrated: false }));
  await assert.rejects(() => fake.firstMove(hand, { ...frame(), moveScale: NaN }));
  await assert.rejects(() => fake.firstMove(hand, { ...frame(), cards: frame().cards.reverse() }));
  assert.equal(starts, 1); assert.equal(sent.length, 0);
  const aim = { calibrated: true, observedAt: Date.now(), cursor: { x: 100, y: 600 }, card: { x: 210, y: 820 }, moveScale: 5.5 };
  await fake.aimAtCard(aim);
  assert.deepEqual(sent.splice(0), ['move 20 40']);
  const drag = { calibrated: true, observedAt: Date.now(), cursor: { x: 210, y: 820 },
    cardBounds: { left: 200, top: 800, right: 230, bottom: 900 }, target: { x: 210, y: 630 }, dragScale: 3.8 };
  await assert.rejects(() => fake.dragObservedCard({ ...drag, cursor: { x: 199, y: 820 } }), /not confirmed/);
  await assert.rejects(() => fake.dragObservedCard({ ...drag, observedAt: Date.now() - 10000 }));
  assert.equal(sent.length, 0);
  await fake.dragObservedCard(drag);
  assert.deepEqual(sent.splice(0), ['play 0 0 0 -50']);

  // Optional read-only integration: independent of whichever game is current.
  if (process.env.DURAK_LIVE_SMOKE !== '1') {
    console.log('PASS: fixed button mappings; one-call first move; freshness/calibration guards. No HID input sent.');
    return;
  }
  const client = new DurakSession({ hid: false, showCards: false,
    dll: process.env.DURAK_TEST_DLL || process.env.DURAK_LINK_DLL || require('node:path').join(__dirname, 'build', 'DurakMouse.dll') });
  try {
    await client.ready();
    const status = await client.cards.request({ op: 'status' });
    await assert.rejects(() => client.cards.request({ op: 'invalid-test' }));
    assert.equal((await client.cards.request({ op: 'status' })).GameId, status.GameId);
  } finally { client.close(); }
  console.log('PASS: fixed button mappings; one-call first move; freshness/calibration guards; persistent responses. No HID input sent.');
})().catch(error => { console.error(error); process.exitCode = 1; });
