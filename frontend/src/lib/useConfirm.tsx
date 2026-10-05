import { useCallback, useState } from 'react'
import { ConfirmDialog, type ConfirmOptions } from '../components/ui'

/** `await confirm({...})` resolves to true only if the user confirmed; render `dialog` in the page. */
export function useConfirm() {
  const [pending, setPending] = useState<(ConfirmOptions & { resolve: (ok: boolean) => void }) | null>(null)
  const confirm = useCallback((options: ConfirmOptions) =>
    new Promise<boolean>((resolve) => setPending({ ...options, resolve })), [])
  const close = (ok: boolean) => {
    pending?.resolve(ok)
    setPending(null)
  }
  const dialog = pending && (
    <ConfirmDialog {...pending} onConfirm={() => close(true)} onCancel={() => close(false)} />
  )
  return { confirm, dialog }
}
