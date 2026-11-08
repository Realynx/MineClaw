'use client';

import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/primitives';
import { Dialog } from '@/components/ui/overlays';
import { errorText } from '@/lib/api/client';
import { useApiMutation } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';

/** Library deletion never queues demolition or stops an already placed build. */
export function DeleteDesign({ id, name, onDeleted }: { id: number; name: string; onDeleted?: () => void }) {
  const { can } = useAuth();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const remove = useApiMutation('designer.deleteDesign', {
    onSuccess: async () => {
      await qc.invalidateQueries({ queryKey: ['designs.list'] });
      setOpen(false);
      onDeleted?.();
    },
  });
  if (!can('design')) return null;
  return <>
    <Button size="sm" variant="ghost" aria-label={`Delete ${name} from library`} onClick={() => { remove.reset(); setOpen(true); }}>
      <Trash2 className="size-3.5" /> Delete
    </Button>
    <Dialog open={open} onOpenChange={(v) => { if (!remove.isPending) setOpen(v); }} title="Delete saved design?"
      description={`Remove “${name}” (#${id}) from your design library.`}
      footer={<>
        <Button variant="ghost" disabled={remove.isPending} onClick={() => setOpen(false)}>Cancel</Button>
        <Button variant="danger" disabled={remove.isPending} onClick={() => remove.mutate({ params: { id }, body: { name } })}>
          {remove.isPending ? 'Deleting…' : 'Delete from library'}
        </Button>
      </>}>
      <p className="text-sm text-muted">Existing buildings and their build and repair tasks stay unchanged. This removes the design from the saved library and placement palette.</p>
      {remove.error ? <p role="alert" className="mt-3 text-sm text-danger">{errorText(remove.error)}</p> : null}
    </Dialog>
  </>;
}
