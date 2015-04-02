﻿using NetworkSocket.Fast.Attributes;
using NetworkSocket.Fast.Filters;
using NetworkSocket.Fast.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;


namespace NetworkSocket.Fast
{
    /// <summary>
    /// 快速构建Tcp服务端抽象类 
    /// </summary>
    public abstract class FastTcpServerBase : TcpServerBase<FastPacket>, IFastTcpServer, IAuthorizationFilter, IActionFilter, IExceptionFilter
    {
        /// <summary>
        /// 所有服务行为
        /// </summary>
        private List<FastAction> fastActionList;

        /// <summary>
        /// 获取或设置序列化工具
        /// 默认是Json序列化
        /// </summary>
        public ISerializer Serializer { get; set; }

        /// <summary>
        /// 获取或设置服务行为特性过滤器提供者
        /// </summary>
        public IFilterAttributeProvider FilterAttributeProvider { get; set; }

        /// <summary>
        /// 快速构建Tcp服务端
        /// </summary>
        public FastTcpServerBase()
        {
            this.fastActionList = new List<FastAction>();
            this.Serializer = new DefaultSerializer();
            this.FilterAttributeProvider = new FilterAttributeProvider();

            // 添加自身到全局过滤器
            GlobalFilters.Add(this);
        }

        /// <summary>
        /// 绑定本程序集所有实现IFastService的服务
        /// </summary>
        /// <returns></returns>
        public FastTcpServerBase BindService()
        {
            var allServices = this.GetType().Assembly.GetTypes().Where(item => typeof(IFastService).IsAssignableFrom(item));
            return this.BindService(allServices);
        }

        /// <summary>
        /// 绑定服务
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <returns></returns>
        public FastTcpServerBase BindService<T>() where T : IFastService
        {
            return this.BindService(typeof(T));
        }

        /// <summary>
        /// 绑定服务
        /// </summary>
        /// <param name="serviceType">服务类型</param>
        /// <returns></returns>
        public FastTcpServerBase BindService(params Type[] serviceType)
        {
            return this.BindService((IEnumerable<Type>)serviceType);
        }

        /// <summary>
        /// 绑定服务
        /// </summary>
        /// <param name="serivceType">服务类型</param>
        /// <returns></returns>
        public FastTcpServerBase BindService(IEnumerable<Type> serivceType)
        {
            if (serivceType == null)
            {
                throw new ArgumentNullException("serivceType");
            }

            if (serivceType.Any(item => item == null))
            {
                throw new ArgumentException("serivceType不能含null值");
            }

            if (serivceType.Any(item => typeof(IFastService).IsAssignableFrom(item) == false))
            {
                throw new ArgumentException("serivceType必须派生于IFastService");
            }

            foreach (var type in serivceType)
            {
                var actions = FastTcpCommon.GetServiceActions(type);
                this.fastActionList.AddRange(actions);
            }

            FastTcpCommon.CheckActionsRepeatCommand(this.fastActionList);
            FastTcpCommon.CheckActionsTaskOrVoid(this.fastActionList);

            return this;
        }



        /// <summary>
        /// 获取服务实例
        /// 并赋值给服务实例的FastTcpServer属性
        /// </summary>
        /// <typeparam name="T">服务类型</typeparam>
        /// <returns></returns>
        public T GetService<T>() where T : IFastService
        {
            return (T)this.GetService(typeof(T));
        }

        /// <summary>
        /// 获取服务实例
        /// 并赋值给服务实例的FastTcpServer属性
        /// </summary>
        /// <param name="serviceType">服务类型</param>
        /// <returns></returns>
        private IFastService GetService(Type serviceType)
        {
            var fastService = DependencyResolver.Current.GetService(serviceType) as IFastService;
            fastService.FastTcpServer = this;
            return fastService;
        }

        /// <summary>
        /// 当接收到远程端的数据时，将触发此方法
        /// 此方法用于处理和分析收到的数据
        /// 如果得到一个数据包，将触发OnRecvComplete方法
        /// [注]这里只需处理一个数据包的流程
        /// </summary>
        /// <param name="client">客户端</param>
        /// <param name="recvBuilder">接收到的历史数据</param>
        /// <returns>如果不够一个数据包，则请返回null</returns>
        protected override FastPacket OnReceive(SocketAsync<FastPacket> client, ByteBuilder recvBuilder)
        {
            return FastPacket.GetPacket(recvBuilder);
        }

        /// <summary>
        /// 当接收到客户端数据包时，将触发此方法
        /// </summary>
        /// <param name="client">客户端</param>
        /// <param name="packet">数据包</param>
        protected override void OnRecvComplete(SocketAsync<FastPacket> client, FastPacket packet)
        {
            if (packet.IsException == false)
            {
                this.ProcessNormalPacket(client, packet);
                return;
            }

            // 抛出远程异常
            var exception = FastTcpCommon.ThrowRemoteException(packet, this.Serializer);
            if (exception != null)
            {
                var exceptionContext = new ExceptionContext { Client = client, Packet = packet, Exception = exception };
                this.OnException(exceptionContext);
            }
        }

        /// <summary>
        /// 处理正常数据包
        /// </summary>
        /// <param name="client">客户端</param>
        /// <param name="packet">数据包</param>
        private void ProcessNormalPacket(SocketAsync<FastPacket> client, FastPacket packet)
        {
            var requestContext = new RequestContext { Client = client, Packet = packet };
            var action = this.GetFastAction(requestContext);

            if (action == null)
            {
                return;
            }

            var actionContext = new ActionContext(requestContext, action);
            var fastService = this.GetFastService(actionContext);

            if (fastService == null)
            {
                return;
            }

            // 执行服务行为          
            fastService.Execute(actionContext);

            // 释放资源
            DependencyResolver.Current.TerminateService(fastService);
        }

        /// <summary>
        /// 获取服务行为
        /// </summary>
        /// <param name="requestContext">请求上下文</param>
        /// <returns></returns>
        private FastAction GetFastAction(RequestContext requestContext)
        {
            var action = this.fastActionList.Find(item => item.Command == requestContext.Packet.Command);
            if (action != null)
            {
                return action;
            }

            var exception = new Exception(string.Format("命令为{0}的服务行为不存在", requestContext.Packet.Command));
            var exceptionContext = new ExceptionContext(requestContext, exception);
            this.ExecExceptionFilters(exceptionContext);

            if (exceptionContext.ExceptionHandled == false)
            {
                throw exception;
            }

            return null;
        }

        /// <summary>
        /// 获取FastService实例
        /// </summary>
        /// <param name="actionContext">服务行为上下文</param>
        /// <returns></returns>
        private IFastService GetFastService(ActionContext actionContext)
        {
            // 获取服务实例
            var fastService = this.GetService(actionContext.Action.DeclaringService);
            if (fastService != null)
            {
                return fastService;
            }
            var exception = new Exception(string.Format("无法获取类型{0}的实例", actionContext.Action.DeclaringService));
            var exceptionContext = new ExceptionContext(actionContext, exception);
            this.ExecExceptionFilters(exceptionContext);

            if (exceptionContext.ExceptionHandled == false)
            {
                throw exception;
            }

            return null;
        }


        #region IFilter
        /// <summary>
        /// 获取或设置排序
        /// </summary>
        public int Order
        {
            get
            {
                return 0;
            }
        }

        /// <summary>
        /// 是否允许多个实例
        /// </summary>
        public bool AllowMultiple
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// 授权时触发       
        /// </summary>
        /// <param name="filterContext">上下文</param>       
        /// <returns></returns>
        public virtual void OnAuthorization(ActionContext filterContext)
        {
        }

        /// <summary>
        /// 在执行服务行为前触发       
        /// </summary>
        /// <param name="filterContext">上下文</param>       
        /// <returns></returns>
        public virtual void OnExecuting(ActionContext filterContext)
        {
        }

        /// <summary>
        /// 在执行服务行为后触发
        /// </summary>
        /// <param name="filterContext">上下文</param>      
        public virtual void OnExecuted(ActionContext filterContext)
        {
        }

        /// <summary>
        /// 异常时触发
        /// </summary>
        /// <param name="filterContext">上下文</param>
        public virtual void OnException(ExceptionContext filterContext)
        {
        }
        #endregion

        #region IDisponse
        /// <summary>
        /// 释放资源
        /// </summary>
        /// <param name="disposing">是否也释放托管资源</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                this.fastActionList.Clear();
                this.fastActionList = null;
                this.Serializer = null;
                this.FilterAttributeProvider = null;
            }
        }
        #endregion

    }
}