import ConfirmDialog from '~/components/ConfirmDialog.vue'

type ConfirmOptions = {
  title: string
  description?: string
  confirmLabel?: string
  danger?: boolean
}

/** Call in setup; the returned function opens the dialog and resolves to the operator's answer. */
export function useConfirm() {
  const dialog = useOverlay().create(ConfirmDialog)

  return async (options: ConfirmOptions) => Boolean(await dialog.open(options))
}
