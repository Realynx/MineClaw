import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { TooltipProvider } from '@/components/ui/overlays';
import { FidelityChip } from '@/features/memory-activity/cloud/CloudView';
import { memoryCloud } from '@/mocks/data/cloud';

describe('cloud fidelity badge', () => {
  it('shows how true the distances are, with the honest explanation on hover', async () => {
    render(
      <TooltipProvider>
        <FidelityChip arrangement="mds" cloud={memoryCloud('mds')} />
      </TooltipProvider>,
    );
    const badge = screen.getByTestId('cloud-fidelity');
    expect(badge.textContent).toBe('Distances: true to embeddings (r = 0.86, stress 0.12)');
    await userEvent.hover(badge);
    expect((await screen.findAllByText(/no flat picture can keep every distance/)).length).toBeGreaterThan(0);
  });

  it('says UMAP is not to be read for distances, and shows nothing for a layout of another arrangement', () => {
    const { rerender } = render(
      <TooltipProvider>
        <FidelityChip arrangement="umap" cloud={memoryCloud('umap')} />
      </TooltipProvider>,
    );
    expect(screen.getByTestId('cloud-fidelity').textContent).toMatch(/^Distances: roughly to scale \(r = 0\.66/);
    rerender(
      <TooltipProvider>
        <FidelityChip arrangement="pca" cloud={memoryCloud('umap')} />
      </TooltipProvider>,
    );
    expect(screen.queryByTestId('cloud-fidelity')).toBeNull();
  });
});
