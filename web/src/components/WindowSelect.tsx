import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { describeWindow, windowsWith } from '@/lib/urls/churnParams'

/**
 * How far back a view counts, as a labelled select. The churn page and the overview each offer
 * their own windows, plus whatever the URL carries, so a hand-written `days` shows as itself rather
 * than as a blank select; both name a window through `describeWindow`, so the select and a heading
 * cannot disagree.
 */
export function WindowSelect({
  id,
  offered,
  days,
  onChange,
}: {
  id: string
  offered: readonly number[]
  days: number
  onChange: (days: number) => void
}) {
  return (
    <div className="flex items-center gap-2">
      <Label htmlFor={id} className="text-xs text-muted-foreground">
        Window
      </Label>
      <Select value={String(days)} onValueChange={(next) => onChange(Number(next))}>
        <SelectTrigger id={id} className="w-36">
          <SelectValue>{describeWindow(days)}</SelectValue>
        </SelectTrigger>
        <SelectContent>
          {windowsWith(offered, days).map((window) => (
            <SelectItem key={window} value={String(window)}>
              {describeWindow(window)}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  )
}
