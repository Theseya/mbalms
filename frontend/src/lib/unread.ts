const EVENT = 'mbalms:notifications-changed'

/** Tells the layout to reload the unread counter after notifications were marked as read. */
export function notifyUnreadChanged() {
  window.dispatchEvent(new Event(EVENT))
}

export function onUnreadChanged(listener: () => void): () => void {
  window.addEventListener(EVENT, listener)
  return () => window.removeEventListener(EVENT, listener)
}
