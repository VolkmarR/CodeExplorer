import type { ToolTotals } from '@/features/toolCalls/api'
import { formatCount, formatSeconds } from '@/lib/format'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

/**
 * The time of day only: the counts go back no further than the server's last start, and a full date
 * made the table wider than its card.
 */
const clock = new Intl.DateTimeFormat(undefined, { timeStyle: 'medium' })

/** Every tool called since the server started, most called first as the server sorts them. */
export function ToolTable({ tools }: { tools: ToolTotals[] }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Tool</TableHead>
          <TableHead className="text-right">Calls</TableHead>
          <TableHead className="text-right">Failed</TableHead>
          <TableHead className="text-right">p50</TableHead>
          <TableHead className="text-right">p95</TableHead>
          <TableHead className="text-right">Max</TableHead>
          <TableHead>Last call</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody className="tabular-nums">
        {tools.map((tool) => (
          <TableRow key={tool.tool}>
            <TableCell className="font-mono">{tool.tool}</TableCell>
            <TableCell className="text-right">{formatCount(tool.calls)}</TableCell>
            <TableCell className={tool.failed > 0 ? 'text-right text-destructive' : 'text-right'}>
              {formatCount(tool.failed)}
            </TableCell>
            <TableCell className="text-right">{formatSeconds(tool.p50Seconds)}</TableCell>
            <TableCell className="text-right">{formatSeconds(tool.p95Seconds)}</TableCell>
            <TableCell className="text-right">{formatSeconds(tool.maxSeconds)}</TableCell>
            <TableCell className="text-muted-foreground">
              {clock.format(new Date(tool.lastCall))}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}
