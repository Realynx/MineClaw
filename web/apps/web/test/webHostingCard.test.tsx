import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { WebTlsSettings, type WebHostingResponse } from '@mineclaw/contracts';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { WebHostingCard } from '@/features/administration/WebHostingCard';
import { ApiError } from '@/lib/api/client';
import type * as ApiClient from '@/lib/api/client';

const mock = vi.hoisted(() => ({ data: {} as WebHostingResponse, api: vi.fn(), refetch: vi.fn(), stepUp: vi.fn() }));
vi.mock('@/lib/api/query', () => ({ useApi: () => ({ data: mock.data, error: null, refetch: mock.refetch }) }));
vi.mock('@/lib/api/client', async (original) => ({
  ...(await original<typeof ApiClient>()),
  api: mock.api,
}));
vi.mock('@/features/administration/useStepUp', () => ({
  StepUpCancelled: class extends Error {},
  useStepUp: () => ({ run: mock.stepUp, dialog: null }),
}));

beforeEach(() => {
  vi.clearAllMocks();
  const tls = WebTlsSettings.parse({});
  mock.data = {
    running: tls,
    saved: tls,
    pendingRestart: false,
    editable: true,
    overridePath: '/data/hosting.override.json',
    httpsOrigin: null,
    savedOrigin: null,
  };
  mock.api.mockResolvedValue(mock.data);
  mock.stepUp.mockImplementation((fn: () => Promise<unknown>) => fn());
});

describe('web certificate settings', () => {
  it('reviews the actual HTTPS diff and saves through step-up without restarting any service', async () => {
    render(<WebHostingCard />);
    fireEvent.click(screen.getByRole('checkbox', { name: /additional HTTPS/ }));
    expect(screen.getByRole('button', { name: 'Review changes' })).toBeDisabled();
    for (const [label, value] of [
      ['Hostname', 'colony.example'],
      ['Certificate chain path', '/etc/tls/fullchain.pem'],
      ['Private key path', '/etc/tls/private.pem'],
    ]) {
      fireEvent.change(screen.getByLabelText(label!), { target: { value } });
    }
    fireEvent.click(screen.getByRole('button', { name: 'Review changes' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('restarting only mineclaw-web-api');
    expect(screen.getByRole('table', { name: 'Changes' })).toHaveTextContent('off');
    expect(screen.getByRole('button', { name: 'Save 4 changes' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'Install the app over HTTPS' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save 4 changes' }));
    await waitFor(() => expect(mock.api).toHaveBeenCalledOnce());
    expect(mock.stepUp).toHaveBeenCalledOnce();
    expect(mock.api).toHaveBeenCalledWith('admin.webHostingSave', {
      body: {
        tls: {
          enabled: true,
          hostname: 'colony.example',
          port: 8443,
          certFile: '/etc/tls/fullchain.pem',
          keyFile: '/etc/tls/private.pem',
        },
        reason: 'Install the app over HTTPS',
      },
    });
    await waitFor(() => expect(mock.refetch).toHaveBeenCalledOnce());
  });

  it('distinguishes saved settings from the listener that is actually running', () => {
    mock.data = {
      ...mock.data,
      pendingRestart: true,
      savedOrigin: 'https://colony.example:8443',
      saved: {
        ...mock.data.saved,
        enabled: true,
        hostname: 'colony.example',
        certFile: '/cert.pem',
        keyFile: '/key.pem',
      },
    };
    render(<WebHostingCard />);
    expect(screen.getByRole('status')).toHaveTextContent('Restart the web API service');
    expect(screen.getByRole('status')).toHaveTextContent('https://colony.example:8443');
    expect(screen.getByRole('status')).toHaveTextContent('bot restart button below does not apply');
    expect(screen.getByText(/Direct HTTPS is currently off/)).toBeInTheDocument();
    expect(mock.api).not.toHaveBeenCalled();
  });

  it('does not offer editable settings while environment overrides take precedence', () => {
    mock.data = { ...mock.data, editable: false };
    render(<WebHostingCard />);
    expect(screen.getByLabelText('Hostname')).toBeDisabled();
    expect(screen.getByRole('checkbox')).toBeDisabled();
    expect(screen.getByText(/controlled by environment variables/)).toBeInTheDocument();
  });

  it('shows the concrete certificate validation error inside the review dialog and retains the draft', async () => {
    mock.api.mockRejectedValueOnce(
      new ApiError(400, 'Invalid HTTPS configuration', {
        code: 'bad_request',
        details: [
          { path: 'hostname', message: 'The certificate subject alternative names do not cover this hostname' },
        ],
      }),
    );
    render(<WebHostingCard />);
    fireEvent.change(screen.getByLabelText('HTTPS port'), { target: { value: '8444' } });
    fireEvent.click(screen.getByRole('button', { name: 'Review changes' }));
    fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'Use another listener port' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save 1 change' }));
    await waitFor(() =>
      expect(screen.getByRole('dialog')).toHaveTextContent(
        'Hostname: The certificate subject alternative names do not cover this hostname',
      ),
    );
    expect(screen.getByLabelText('Reason')).toHaveValue('Use another listener port');
    expect(mock.refetch).not.toHaveBeenCalled();
  });
});
