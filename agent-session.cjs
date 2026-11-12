'use strict';
// Persistent local API for the agent. No screen recognition and no blind join/ready loop.
const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const { performance } = require('node:perf_hooks');
const DLL = process.env.DURAK_LINK_DLL || require('node:path').join(__dirname, 'build', 'DurakMouse.dll');

class Wire {
  constructor(args, json, dll = DLL) {
    this.json = json; this.pending = null; this.tail = Promise.resolve();
    this.process = spawn('dotnet', [dll, ...args], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.lines = []; this.ready = new Promise((resolve, reject) => {
      this.resolveReady = resolve; this.rejectReady = reject;
      this.startTimer = setTimeout(() => reject(Error('Service did not start')), 5000);
    });
    this.process.on('error', error => this.fail(error));
    this.process.stdin.on('error', error => this.fail(error));
    this.process.on('exit', code => this.fail(Error('Service exited: ' + code)));
    createInterface({ input: this.process.stdout }).on('line', line => this.onLine(line));
    createInterface({ input: this.process.stderr }).on('line', line => this.lines.push(line));
  }
  onLine(line) {
    this.lines.push(line); if (this.lines.length > 500) this.lines.shift();
    if ((this.json && line.includes('"Protocol":"durak-cards-v1"')) || (!this.json && line.startsWith('Commands:'))) {
      clearTimeout(this.startTimer); this.resolveReady(); return;
    }
    const p = this.pending; if (!p) return;
    if (this.json) {
      try {
        const data = JSON.parse(line); this.pending = null; clearTimeout(p.timer);
        if (data.Error) p.reject(Error(data.Error)); else p.resolve(data);
      } catch (error) { this.fail(error); }
    } else {
      p.lines.push(line);
      if (line === 'DONE ' + p.command) {
        this.pending = null; clearTimeout(p.timer);
        const failed = p.lines.some(x => /Unknown command|out of range|Expected exactly/.test(x) || /Input report: (?!Success)/.test(x));
        p.resolve({ delivered: !failed, elapsedMs: Math.round(performance.now() - p.started), lines: p.lines });
      }
    }
  }
  request(value) {
    const run = async () => {
      await this.ready;
      if (this.broken) throw this.broken;
      const command = this.json ? JSON.stringify(value) : value;
      if (typeof command !== 'string' || command.includes('\n')) throw Error('One command per request');
      return await new Promise((resolve, reject) => {
        this.pending = { resolve, reject, command, lines: [], started: performance.now(), timer: setTimeout(() => this.fail(Error('Command timed out; inspect screen before retry')), 5000) };
        this.process.stdin.write(command + '\n');
      });
    };
    const next = this.tail.then(run); this.tail = next.catch(() => {}); return next;
  }
  fail(error) {
    this.broken = error; clearTimeout(this.startTimer); this.rejectReady(error);
    if (this.pending) { clearTimeout(this.pending.timer); this.pending.reject(error); this.pending = null; }
  }
  close() { if (this.process.exitCode === null && !this.process.stdin.destroyed) this.process.stdin.end('quit\n'); }
}

class DurakSession {
  constructor({ showCards = true, hid = true, dll = DLL } = {}) {
    this.cards = new Wire(['--cards-server'], true, dll);
    if (hid) this.mouse = new Wire(['--advertise'], false, dll);
    if (showCards) this.window = spawn(dll.replace(/\.dll$/, '.exe'), ['--cards-window'], { windowsHide: false, stdio: 'ignore' });
  }
  async ready() { await this.cards.ready; if (this.mouse) await this.mouse.ready; }
  async button(name) {
    const commands = { open: 'lobby-open', first: 'lobby-first', ready: 'lobby-ready', take: 'table-take', done: 'table-done' };
    if (!commands[name] || !this.mouse) throw Error('Unknown button or mouse unavailable');
    // Caller must have just seen this button on the live iPhone screen.
    return await this.mouse.request(commands[name]);
  }
  async startHand(hand) { return await this.cards.request({ op: 'start', hand }); }
  async apply(batch) { return await this.cards.request({ op: 'apply', batch }); }
  async decide(request) { return await this.cards.request({ op: 'decide', request }); }
  async decisionContext() {
    const state = await this.cards.request({ op: 'status' });
    if (!state.GameId || !state.Reliable) throw Error('Нет надёжного учёта текущей раздачи');
    return {
      gameId: state.GameId, revision: state.Revision, stock: state.Stock, table: state.Table,
      discard: state.Discard, unknown: state.UnknownCards,
      opponents: state.Players.slice(1).map(player => ({
        name: player.Name, count: player.Count, known: player.Known,
        likely: player.CertainOr90Percent, unknownProbability: player.UnknownCardProbability
      }))
    };
  }
  // Two observed frames are required: movement delivery alone is not proof of pointer position.
  async aimAtCard(frame) {
    if (!this.mouse || !frame || frame.calibrated !== true || Date.now() - frame.observedAt > 6000 || frame.observedAt > Date.now() + 1000)
      throw Error('Need a fresh frame and verified pointer calibration');
    const { cursor, card, moveScale } = frame;
    if (!cursor || !card || ![cursor.x, cursor.y, card.x, card.y, moveScale].every(Number.isFinite) || moveScale <= 0)
      throw Error('Invalid pointer or card coordinates');
    const dx = Math.round((card.x - cursor.x) / moveScale), dy = Math.round((card.y - cursor.y) / moveScale);
    if ([dx, dy].some(n => n < -127 || n > 127)) throw Error('Move exceeds HID range; reobserve after an intermediate move');
    return await this.mouse.request(`move ${dx} ${dy}`);
  }
  async dragObservedCard(frame) {
    if (!this.mouse || !frame || frame.calibrated !== true || Date.now() - frame.observedAt > 6000 || frame.observedAt > Date.now() + 1000)
      throw Error('Need a fresh frame and verified pointer calibration');
    const { cursor, cardBounds, target, dragScale } = frame;
    if (!cursor || !cardBounds || !target || ![cursor.x, cursor.y, cardBounds.left, cardBounds.top, cardBounds.right, cardBounds.bottom, target.x, target.y, dragScale].every(Number.isFinite) || dragScale <= 0)
      throw Error('Invalid observed drag coordinates');
    if (cardBounds.left >= cardBounds.right || cardBounds.top >= cardBounds.bottom || cursor.x < cardBounds.left || cursor.x > cardBounds.right || cursor.y < cardBounds.top || cursor.y > cardBounds.bottom)
      throw Error('Pointer not confirmed on the intended card');
    const dx = Math.round((target.x - cursor.x) / dragScale), dy = Math.round((target.y - cursor.y) / dragScale);
    if (dy >= 0 || [dx, dy].some(n => n < -127 || n > 127)) throw Error('Unsafe drag direction or distance');
    return await this.mouse.request(`play 0 0 ${dx} ${dy}`);
  }
  async firstMove(hand, frame) {
    // Coordinates and calibration must come from the same fresh observed screen.
    if (!this.mouse || !frame || frame.calibrated !== true || Date.now() - frame.observedAt > 6000 || frame.observedAt > Date.now() + 1000)
      throw Error('Need a fresh frame and verified pointer calibration');
    if (frame.cards.length !== 6 || frame.cards.map(p => p.card).join() !== hand.Cards.join()) throw Error('Card positions do not match the observed hand');
    for (const value of [frame.cursor.x, frame.cursor.y, frame.targetY, frame.moveScale, frame.dragScale, frame.observedAt, ...frame.cards.flatMap(p => [p.x, p.y])])
      if (!Number.isFinite(value)) throw Error('Invalid coordinates');
    if (frame.moveScale <= 0 || frame.dragScale <= 0) throw Error('Invalid calibration');
    const result = await this.startHand(hand), decision = result.Decision;
    if (decision.Kind !== 'attack' || decision.RequiresReview || !decision.Template.startsWith('FIRST_')) return { ...result, sent: false };
    const card = frame.cards.find(c => c.card === decision.Card);
    const delta = [Math.round((card.x - frame.cursor.x) / frame.moveScale), Math.round((card.y - frame.cursor.y) / frame.moveScale), 0, Math.round((frame.targetY - card.y) / frame.dragScale)];
    if (delta.some(n => n < -127 || n > 127) || delta[3] >= 0 || Date.now() - frame.observedAt > 6000) throw Error('Unsafe or stale first move; refresh the screen');
    const delivery = await this.mouse.request('play ' + delta.join(' '));
    // Delivery is NOT card confirmation. The caller must observe the card on the table,
    // then record play and execution. Never mark the card played here.
    return { ...result, sent: true, delivery };
  }
  async execution(decisionId, status, ms, note) { return await this.cards.request({ op: 'execution', decisionId, status, ms, note }); }
  close() { this.mouse?.close(); this.cards.close(); } // Keep the visible cards/history window available.
}
module.exports = { DurakSession, Wire };
