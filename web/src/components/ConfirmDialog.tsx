import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'

/**
 * The question before something is destroyed. One component for every destructive action, so each
 * asks the same way: the title names the thing, the description says what goes with it, and the
 * confirm button repeats the verb — never "OK", which is what the browser's own dialog offered and
 * what a reader in a hurry presses without reading.
 *
 * The trigger is rendered here rather than passed in, so no caller can put the button somewhere and
 * forget the dialog.
 */
export function ConfirmDialog({
  trigger,
  title,
  description,
  action,
  disabled = false,
  onConfirm,
}: {
  trigger: React.ReactNode
  title: string
  description: React.ReactNode
  /** The verb on the confirm button, e.g. "Delete project". */
  action: string
  disabled?: boolean
  onConfirm: () => void
}) {
  return (
    <AlertDialog>
      <AlertDialogTrigger render={<Button variant="destructive" disabled={disabled} />}>
        {trigger}
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={onConfirm}>
            {action}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
