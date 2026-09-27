export type QueuedMessage = { id: string; text: string };
type Snapshot = { pending: readonly QueuedMessage[]; running: boolean; paused: boolean };

/** One AG-UI run at a time, while the person can keep composing. */
export class ConversationQueue {
  private state: Snapshot = { pending: [], running: false, paused: false };
  private listeners = new Set<() => void>();
  getSnapshot = () => this.state;
  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };
  private update(patch: Partial<Snapshot>) {
    this.state = { ...this.state, ...patch };
    for (const listener of this.listeners) listener();
  }
  enqueue(message: QueuedMessage) {
    this.update({ pending: [...this.state.pending, message] });
  }
  remove(id: string) {
    this.update({ pending: this.state.pending.filter((message) => message.id !== id) });
  }
  pause() {
    this.update({ paused: true });
  }
  resume() {
    this.update({ paused: false });
  }
  async flush(send: (message: QueuedMessage) => Promise<void>) {
    if (this.state.running || this.state.paused) return;
    this.update({ running: true });
    try {
      while (this.state.pending.length && !this.state.paused) {
        const [message, ...pending] = this.state.pending;
        this.update({ pending });
        await send(message);
      }
    } catch (error) {
      // The failed message is already in the transcript. Never resend it implicitly.
      this.update({ paused: true });
      throw error;
    } finally {
      this.update({ running: false });
    }
  }
}
