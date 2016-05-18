﻿using Dia2Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CsDebugScript.CodeGen.UserTypes
{
    class TemplateUserTypeFactory : UserTypeFactory
    {
        public TemplateUserTypeFactory(UserTypeFactory factory, TemplateUserType templateType)
            : base(factory)
        {
            TemplateType = templateType;
            OriginalFactory = factory;
        }

        public TemplateUserType TemplateType { get; private set; }

        public UserTypeFactory OriginalFactory { get; private set; }

        internal override bool GetUserType(Symbol type, out UserType userType)
        {
            string argumentName;
            string typeString = type.Name;

            if (TryGetArgument(typeString, out argumentName))
            {
                //#fixme invesitage this
                userType = new TemplateArgumentUserType(argumentName, type);
                return true;
            }

            return base.GetUserType(type, out userType);
        }

        internal override bool TryGetUserType(Module module, string typeString, out UserType userType)
        {
            string argumentName;

            if (TryGetArgument(typeString, out argumentName))
            {
                //#fixme invesitage this
                userType = new TemplateArgumentUserType(argumentName, null);
                return true;
            }

            return base.TryGetUserType(module, typeString, out userType);
        }

        private bool TryGetArgument(string typeString, out string argumentName)
        {
            if (TemplateType.TryGetTemplateArgument(typeString, out argumentName))
            {
                return true;
            }

            if (typeString == "wchar_t")
            {
                if (TemplateType.TryGetTemplateArgument("unsigned short", out argumentName))
                    return true;
            }
            else if (typeString == "unsigned short")
            {
                if (TemplateType.TryGetTemplateArgument("whcar_t", out argumentName))
                    return true;
            }
            else if (typeString == "unsigned long long")
            {
                if (TemplateType.TryGetTemplateArgument("unsigned __int64", out argumentName))
                    return true;
            }
            else if (typeString == "unsigned __int64")
            {
                if (TemplateType.TryGetTemplateArgument("unsigned long long", out argumentName))
                    return true;
            }
            else if (typeString == "long long")
            {
                if (TemplateType.TryGetTemplateArgument("__int64", out argumentName))
                    return true;
            }
            else if (typeString == "__int64")
            {
                if (TemplateType.TryGetTemplateArgument("long long", out argumentName))
                    return true;
            }

            return false;
        }
    }
}
