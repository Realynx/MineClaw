/**
 * The Nest controller generated from the contracts `ROUTES` table: one handler per route id, registered in table order
 * (specific paths before parameterised siblings), each delegating to the dispatcher pipeline. A route added to the
 * contracts table is served without touching this file (bridge routes forward; Nest-native ones need a handler).
 */
import { Controller, Get, Inject, Post, Req, Res, type Type } from '@nestjs/common';
import type { ServerResponse } from 'node:http';
import { ROUTES, ROUTE_IDS, type RouteDef, type RouteId } from '@mineclaw/contracts';
import { type RouteDispatcher, type RoutedRequest } from './dispatcher.js';

export const DISPATCHER = Symbol('RouteDispatcher');

export function handlerName(index: number, id: RouteId): string {
  return `route${String(index).padStart(3, '0')}_${id.replace(/[^A-Za-z0-9]/g, '_')}`;
}

export function buildRouteTableController(ids: readonly RouteId[] = ROUTE_IDS): Type<unknown> {
  class RouteTableController {
    constructor(readonly dispatcher: RouteDispatcher) {}
  }
  Inject(DISPATCHER)(RouteTableController, undefined, 0);
  const proto = RouteTableController.prototype as unknown as Record<string, unknown>;
  ids.forEach((id, i) => {
    const route: RouteDef = ROUTES[id];
    const name = handlerName(i, id);
    const handler = function (this: RouteTableController, req: RoutedRequest, res: ServerResponse): Promise<void> {
      return this.dispatcher.dispatch(id, req, res);
    };
    Object.defineProperty(proto, name, { value: handler, writable: true, configurable: true, enumerable: false });
    const descriptor = Object.getOwnPropertyDescriptor(proto, name)!;
    Req()(proto, name, 0);
    Res()(proto, name, 1);
    (route.method === 'GET' ? Get : Post)(route.path)(proto, name, descriptor);
  });
  Controller()(RouteTableController);
  return RouteTableController;
}
